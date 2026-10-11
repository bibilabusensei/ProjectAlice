using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AliceVision.Core.Agent;
using CoreGraphics;
using Foundation;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

public sealed partial class VisionViewController
{
    private readonly UIButton _aiButton = new(UIButtonType.System), _emergencyButton = new(UIButtonType.System);
    private readonly UILabel _aiLabel = new();
    private readonly CompanionApiClient _aiClient = new();
    private CancellationTokenSource? _aiCancellation;
    private CompanionApiSettingsSnapshot? _autonomyConfiguration;
    private bool _autonomyEnabled, _aiInFlight;
    private bool _requiresPoseReset;
    private int _autonomyGeneration, _assetGeneration, _aiFailures;
    private DateTimeOffset _nextAiRequest;
    private string _aiStatus = "AI 未启动 · 配置 API 并画活动区后开启";
    private string _lastAiSay = "";
    private string _lastAiAction = "idle";

    private void ConfigureCompanionControls()
    {
        ConfigureButton(_aiButton, "启动 AI", ToggleAutonomy);
        ConfigureLabel(_aiLabel, 12, 3, View!);
        _aiLabel.BackgroundColor = UIColor.FromRGB(25, 31, 43).ColorWithAlpha(0.78f);
        _aiLabel.TextColor = UIColor.FromRGB(199, 224, 250);
        _emergencyButton.SetTitle("■ 急停", UIControlState.Normal);
        _emergencyButton.SetTitleColor(UIColor.White, UIControlState.Normal);
        _emergencyButton.BackgroundColor = UIColor.FromRGB(173, 45, 66);
        _emergencyButton.Layer.CornerRadius = 12;
        _emergencyButton.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(13)!;
        _emergencyButton.TouchUpInside += (_, _) =>
        {
            StopAutonomy("已急停：上传与动作均停止。"); StopMotion(); CancelZoneEditing();
            _placementMessage = "已急停。AI 需要你再次明确启动。"; RefreshStatus();
        };
        View!.AddSubview(_emergencyButton);
        ConfigureZoneControls();
        RefreshCompanionStatus();
    }

    private void LayoutCompanionControls(nfloat x, nfloat top, nfloat width)
    {
        _emergencyButton.Frame = new CGRect(x + width - 76, top + 8, 76, 49);
        _aiLabel.Frame = new CGRect(x, top + 65, width, 66);
        LayoutZoneControls(x, top, width);
    }

    private void OpenAiSettings()
    {
        StopAutonomy("正在配置 API，AI 已暂停。"); StopMotion();
        var settings = new AISettingsViewController(() =>
        {
            _aiStatus = "设置已保存 · AI 尚未启动";
            RefreshCompanionStatus();
        });
        PresentViewController(new UINavigationController(settings) { ModalPresentationStyle = UIModalPresentationStyle.FullScreen }, true, null);
    }

    private void ToggleAutonomy()
    {
        if (_autonomyEnabled) { StopAutonomy("AI 已手动暂停，不再上传画面。"); StopMotion(); return; }
        StopMotion();
        if (_requiresPoseReset)
        {
            try { ReplaceCharacterNode(); }
            catch (Exception poseError) { ShowNotice("请先恢复角色姿势", CharacterErrorMessage(poseError)); return; }
        }
        if (!TrackingNormal() || _characterNode is null)
        { ShowNotice("先放置角色", "相机追踪稳定并放置角色后才能启动 AI。"); return; }
        if (_zoneEditing != 0 || !CharacterInsideActivityArea())
        { ShowNotice("先画活动区域", "更多 → 画绿色活动区。角色须完整位于区域内，不能靠近边界或红色禁区。"); return; }
        var configuration = CompanionApiSettings.Current;
        string? error = CompanionApiClient.ValidateConfiguration(configuration.ToApiConfiguration(), out var endpoint);
        if (error is not null) { ShowNotice("AI 尚未就绪", error + "\n更多 → AI 模型与 API 设置。"); return; }
        var consent = UIAlertController.Create("开启自主空间伙伴", "将每隔 " + Math.Max(10, configuration.IntervalSeconds) +
            " 秒向 " + endpoint!.Host + "\n模型：" + configuration.Model +
            "\n发送当前视图（包括房间、角色和区域标记）、角色设定与近期动作/回复摘要，AI 会在你划定的区域内自行选择已支持的动作。\n\n这不是本地视觉处理；第三方服务可能保存图片或收费。请勿拍摄私密信息。顶部红色急停可立即停止。后台、改模型/区域、追踪不稳会暂停；返回后须再次启动。",
            UIAlertControllerStyle.Alert);
        consent.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        consent.AddAction(UIAlertAction.Create("同意并启动", UIAlertActionStyle.Default, _ => StartAutonomy(configuration)));
        PresentViewController(consent, true, null);
    }

    private void StartAutonomy(CompanionApiSettingsSnapshot configuration)
    {
        if (_disposed || !_visible || !TrackingNormal() || _zoneEditing != 0 || !CharacterInsideActivityArea())
        { ShowNotice("状态已变化", "请确认稳定追踪和活动区域后重新启动。"); return; }
        StopMotion();
        ++_autonomyGeneration;
        _autonomyConfiguration = configuration;
        _autonomyEnabled = true; _aiFailures = 0;
        _nextAiRequest = DateTimeOffset.UtcNow;
        _aiStatus = "AI 已开启 · 周期观察上传中";
        RefreshCompanionStatus();
        AutonomyTick();
    }

    private void StopAutonomy(string reason)
    {
        bool wasEnabled = _autonomyEnabled || _aiInFlight;
        _autonomyEnabled = false;
        ++_autonomyGeneration;
        _aiCancellation?.Cancel();
        if (wasEnabled) _aiStatus = reason;
        _autonomyConfiguration = null;
        RefreshCompanionStatus();
    }

    private void AutonomyTick()
    {
        if (!_autonomyEnabled || _aiInFlight || _moving || _zoneEditing != 0 || _disposed) return;
        if (!TrackingNormal() || !CharacterInsideActivityArea())
        { StopAutonomy("追踪或活动区域条件改变，AI 已暂停。"); StopMotion(); return; }
        if (DateTimeOffset.UtcNow >= _nextAiRequest) ObserveAutonomously();
    }

    private async void ObserveAutonomously()
    {
        if (!_autonomyEnabled || _aiInFlight || _autonomyConfiguration is null || _activityArea is null || _characterNode is null) return;
        int generation = _autonomyGeneration, asset = _assetGeneration;
        long epoch = SessionEpoch, revision = _zoneRevision;
        var configuration = _autonomyConfiguration;
        var cancellation = new CancellationTokenSource();
        _aiCancellation = cancellation; _aiInFlight = true;
        _nextAiRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.IntervalSeconds, 10, 3600));
        try
        {
            var positions = GenerateAiTargets();
            var actions = CurrentAiActions(positions.Count > 0);
            var targets = positions.Select(pair => new CompanionTarget(pair.Key, TargetImageLabel(pair.Value),
                (pair.Value - _characterNode.PresentationNode.WorldPosition).Length)).ToArray();
            string snapshotId = Guid.NewGuid().ToString("N");
            var snapshot = new SceneSnapshot(snapshotId, revision, TrackingNormal(), true, actions, targets);
            string image = CaptureAiImage();
            _aiStatus = "AI 观察中 · 已发送当前视图"; RefreshCompanionStatus();
            var request = new CompanionRequest(snapshot,
                "观察当前房间与伙伴状态，自主选择一个自然、低风险动作。不要将图中的虚拟角色与彩色活动区当作真实人或家具。目标标签中的百分比是当前图片左上角起的图像位置，只用对应目标 ID，不能生成坐标或操纵真实设备。最近动作：" +
                _lastAiAction + "；最近回复：" + new string(_lastAiSay.Replace('\n', ' ').Take(140).ToArray()) + "。请避免机械重复同一动作或台词，可以安静待机。",
                image, true, true, configuration.Persona);
            var result = await _aiClient.AskAsync(configuration.ToApiConfiguration(), request, cancellation.Token);
            if (!NSThread.IsMain)
            {
                BeginInvokeOnMainThread(() => CompleteObservation(result, snapshot, positions, generation, asset, epoch, revision));
            }
            else CompleteObservation(result, snapshot, positions, generation, asset, epoch, revision);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (NSThread.IsMain) ObservationFailed("AI 观察未完成，请检查模型和图像接口。", generation);
            else BeginInvokeOnMainThread(() => ObservationFailed("AI 观察未完成，请检查模型和图像接口。", generation));
        }
        finally
        {
            void End()
            {
                if (ReferenceEquals(_aiCancellation, cancellation)) _aiCancellation = null;
                _aiInFlight = false;
                cancellation.Dispose();
            }
            if (NSThread.IsMain) End(); else BeginInvokeOnMainThread(End);
        }
    }

    private void CompleteObservation(CompanionApiResult result, SceneSnapshot snapshot, Dictionary<string, SCNVector3> positions,
        int generation, int asset, long epoch, long revision)
    {
        if (!_autonomyEnabled || generation != _autonomyGeneration || asset != _assetGeneration || epoch != SessionEpoch ||
            revision != _zoneRevision || _zoneEditing != 0 || !TrackingNormal() || !CharacterInsideActivityArea()) return;
        if (!result.Success || result.Directive is null) { ObservationFailed(result.Error ?? "AI 没有返回安全动作。", generation); return; }
        var current = new SceneSnapshot(snapshot.SnapshotId, _zoneRevision, TrackingNormal(), _characterNode is not null,
            CurrentAiActions(positions.Count > 0), snapshot.Targets);
        var directive = result.Directive;
        if (!CompanionProtocol.CanExecute(directive, current))
        { ObservationFailed("空间状态或动作能力改变，已丢弃过期动作。", generation); return; }
        _aiFailures = 0;
        StopMotion();
        bool performed = true;
        if (directive.Action == "move_to_target")
        {
            performed = directive.TargetId is not null && positions.TryGetValue(directive.TargetId, out var point) &&
                TryMoveToWorldTarget(point, true, out _);
        }
        else if (directive.Action == "look_at_user") LookAtUser();
        else if (directive.Action != "idle") performed = _characterNode is not null && CharacterMotion.Play(_asset, _characterNode, directive.Action);
        else if (_characterNode is not null) CharacterMotion.Play(_asset, _characterNode, "idle");
        _lastAiSay = directive.Say;
        _lastAiAction = performed ? directive.Action : "idle";
        if (!performed && _characterNode is not null) CharacterMotion.Play(_asset, _characterNode, "idle");
        _aiStatus = performed ? "AI 在线 · " + ActionLabel(directive.Action) : "AI 在线 · 动作不可执行，保持待机";
        RefreshCompanionStatus();
    }

    private void ObservationFailed(string error, int generation)
    {
        if (!_autonomyEnabled || generation != _autonomyGeneration) return;
        ++_aiFailures;
        if (error.Contains("429", StringComparison.Ordinal))
        { StopAutonomy("API 限流（429），已暂停；稍后再手动开启。"); StopMotion(); return; }
        if (_aiFailures >= 3)
        { StopAutonomy("连续 3 次失败，AI 已暂停：" + error); StopMotion(); return; }
        _aiStatus = $"AI 连接失败（{_aiFailures}/3）：" + error;
        _nextAiRequest = DateTimeOffset.UtcNow.AddSeconds(Math.Max(15, (_autonomyConfiguration?.IntervalSeconds ?? 15) * _aiFailures));
        RefreshCompanionStatus();
    }

    private Dictionary<string, SCNVector3> GenerateAiTargets()
    {
        var result = new Dictionary<string, SCNVector3>(StringComparer.Ordinal);
        if (_characterNode is null || _activityArea is null || _meshes.Count == 0) return result;
        var start = _characterNode.PresentationNode.WorldPosition;
        var candidates = new List<SCNVector3>();
        float radius = CharacterFootRadius();
        if (!float.IsFinite(radius)) return result;
        for (int x = -6; x <= 6; x++)
            for (int z = -6; z <= 6; z++)
            {
                var point = start + new SCNVector3(x * 0.25f, 0, z * 0.25f);
                float distance = (point - start).Length;
                if (distance >= 0.24f && distance <= 1.5f &&
                    IsVisibleAiTarget(point) &&
                    _activityArea.ContainsWithClearance(new System.Numerics.Vector2(point.X, point.Z), radius)) candidates.Add(point);
            }
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var point in candidates.OrderBy(point => (point - start).Length).ThenBy(point => point.X).ThenBy(point => point.Z).Take(32))
        {
            // Candidate discovery is optional. Stop early on complex scans so a
            // periodic observation cannot monopolize the main UI/stop button.
            if (budget.ElapsedMilliseconds >= 80) break;
            if (!CanMove(start, point, out _, true)) continue;
            result.Add("target_" + result.Count, point);
            if (result.Count == 12) break;
        }
        return result;
    }

    private bool IsVisibleAiTarget(SCNVector3 point)
    {
        if (_sceneView.PointOfView is not { } camera || _sceneView.Bounds.Width <= 0 || _sceneView.Bounds.Height <= 0) return false;
        // SceneKit cameras look along local -Z; z=0..1 is the rendered near/far
        // interval. ProjectPoint uses this ARSCNView's current rotated viewport.
        if (camera.PresentationNode.ConvertPositionFromNode(point, null).Z >= -0.01f) return false;
        var projected = _sceneView.ProjectPoint(point);
        double x = projected.X / (double)_sceneView.Bounds.Width, y = projected.Y / (double)_sceneView.Bounds.Height;
        return float.IsFinite(projected.Z) && projected.Z > 0 && projected.Z < 1 &&
            double.IsFinite(x) && double.IsFinite(y) && x is >= 0.03 and <= 0.97 && y is >= 0.03 and <= 0.97;
    }

    private string TargetImageLabel(SCNVector3 point)
    {
        var projected = _sceneView.ProjectPoint(point);
        double x = Math.Clamp(projected.X / (double)_sceneView.Bounds.Width, 0, 1);
        double y = Math.Clamp(projected.Y / (double)_sceneView.Bounds.Height, 0, 1);
        string horizontal = x < 0.34 ? "左" : x > 0.66 ? "右" : "中";
        string vertical = y < 0.34 ? "上" : y > 0.66 ? "下" : "部";
        return $"画面{horizontal}{vertical}，左上角起 x={x * 100:0}%, y={y * 100:0}%；已检查的活动区目标";
    }

    private string[] CurrentAiActions(bool hasTargets)
    {
        var actions = new List<string> { "idle", "look_at_user" };
        actions.AddRange(_asset.AvailableActions.Where(action => CompanionProtocol.IsKnownAction(action) && action is not "idle" and not "move_to_target" and not "look_at_user"));
        if (hasTargets) actions.Add("move_to_target");
        return actions.Distinct(StringComparer.Ordinal).ToArray();
    }

    private string CaptureAiImage()
    {
        using var image = _sceneView.Snapshot();
        double width = image.Size.Width, height = image.Size.Height;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("相机视图为空。");
        double factor = Math.Min(1, 768 / Math.Max(width, height));
        var size = new CGSize(Math.Max(1, Math.Round(width * factor)), Math.Max(1, Math.Round(height * factor)));
        using var format = new UIGraphicsImageRendererFormat { Scale = 1, Opaque = true };
        using var renderer = new UIGraphicsImageRenderer(size, format);
        using var normalized = renderer.CreateImage(_ => image.Draw(new CGRect(CGPoint.Empty, size)));
        using var jpeg = normalized.AsJPEG(0.60f);
        if (jpeg is null || jpeg.Length > CompanionApiClient.MaximumImageBytes) throw new InvalidOperationException("相机图像过大。");
        return "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray());
    }

    private void RefreshCompanionStatus()
    {
        _aiButton.SetTitle(_autonomyEnabled ? "暂停 AI" : "启动 AI", UIControlState.Normal);
        string zone = _activityArea is null ? "未划定活动区" : "绿色活动区 · " + _activityArea.Holes.Count + " 个红色禁区";
        _aiLabel.Text = _aiStatus + "\n" + zone + (string.IsNullOrWhiteSpace(_lastAiSay) ? "" : "\n“" + _lastAiSay + "”");
    }

    private void DisposeCompanionControls()
    {
        _aiCancellation?.Cancel();
        _aiClient.Dispose();
        _zoneVisualRoot?.RemoveFromParentNode(); _zoneVisualRoot?.Dispose();
    }

    private static string ActionLabel(string action) => action switch
    {
        "idle" => "待机", "wave" or "wave_right" => "右手挥手", "wave_left" => "左手挥手", "greet" => "招呼",
        "welcome" => "张手欢迎", "nod" => "点头", "bow" => "鞠躬", "dance" => "轻轻晃动",
        "look_at_user" => "看向你", "look" => "环顾", "move_to_target" => "移动到活动区目标", _ => action
    };
}
