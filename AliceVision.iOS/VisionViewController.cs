using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ARKit;
using AVFoundation;
using CoreGraphics;
using Foundation;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

/// <summary>Local-only LiDAR companion with imported characters and manual actions.</summary>
public sealed class VisionViewController : UIViewController
{
    private const long MaximumModelBytes = 80L * 1024 * 1024;
    private const string MoveActionKey = "Alice.Companion.Move";
    private readonly ARSCNView _sceneView = new();
    private readonly UILabel _status = new(), _diagnosticLabel = new(), _characterLabel = new(), _instruction = new();
    private readonly UIView _panel = new();
    private readonly UIButton _importButton = new(UIButtonType.System), _actionButton = new(UIButtonType.System), _moreButton = new(UIButtonType.System);
    private readonly HashSet<string> _planes = new(), _meshes = new();
    private readonly Dictionary<string, PlaneSurface> _planeSurfaces = new();
    private readonly Dictionary<string, MeshBounds[]> _meshBounds = new();
    private SceneDelegate? _sceneDelegate;
    private NSTimer? _timer;
    private NSObject? _backgroundObserver, _activeObserver;
    private ARAnchor? _characterAnchor, _targetAnchor;
    private SCNNode? _characterAnchorNode, _characterNode, _targetAnchorNode, _targetVisual;
    private CharacterAsset _asset = CharacterFactory.CreateDefault();
    private string? _characterPlaneId, _targetPlaneId;
    private float _height = 0.35f;
    private bool _visible, _moving, _requestingPermission, _showDebug, _disposed;
    private volatile bool _running;
    private long _sessionGeneration;
    private int _importGeneration, _motionGeneration;
    private CancellationTokenSource? _importCancellation;
    private string _trackingMessage = "正在准备相机…";
    private string _placementMessage = "扫一下地面，点击蓝色区域，让小伙伴出现。";

    internal long SessionEpoch => Interlocked.Read(ref _sessionGeneration);
    internal bool IsSessionEpochCurrent(long epoch) => !_disposed && epoch == SessionEpoch;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View!.BackgroundColor = UIColor.Black;
        _sceneView.Scene = new SCNScene();
        _sceneView.AutoenablesDefaultLighting = true;
        _sceneView.AutomaticallyUpdatesLighting = true;
        _sceneView.PreferredFramesPerSecond = 30;
        _sceneDelegate = new SceneDelegate(this);
        _sceneView.Delegate = _sceneDelegate;
        View.AddSubview(_sceneView);
        _sceneView.AddGestureRecognizer(new UITapGestureRecognizer(SelectPlanePoint));
        ConfigureLabel(_status, 13, 2, View);
        _status.BackgroundColor = UIColor.Black.ColorWithAlpha(0.62f);
        ConfigureLabel(_diagnosticLabel, 11, 0, View);
        _diagnosticLabel.BackgroundColor = UIColor.Black.ColorWithAlpha(0.68f);
        _diagnosticLabel.Hidden = true;
        _panel.BackgroundColor = UIColor.FromRGB(20, 23, 34).ColorWithAlpha(0.93f);
        _panel.Layer.CornerRadius = 22;
        View.AddSubview(_panel);
        ConfigureLabel(_characterLabel, 15, 1, _panel);
        _characterLabel.Font = UIFont.BoldSystemFontOfSize(15)!;
        ConfigureLabel(_instruction, 12, 2, _panel);
        _instruction.TextColor = UIColor.FromRGB(199, 207, 225);
        ConfigureButton(_importButton, "导入角色", ImportCharacter);
        ConfigureButton(_actionButton, "角色动作", ShowActions);
        ConfigureButton(_moreButton, "更多", ShowMore);
        _backgroundObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillResignActiveNotification, _ => PauseSession());
        _activeObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidBecomeActiveNotification, _ => { if (_visible) EnsureCameraAndStart(); });
        RefreshStatus();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var bounds = View!.Bounds;
        var safe = View.SafeAreaInsets;
        _sceneView.Frame = bounds;
        nfloat width = (nfloat)Math.Min(580, Math.Max(180, (double)(bounds.Width - safe.Left - safe.Right - 28)));
        nfloat x = safe.Left + (bounds.Width - safe.Left - safe.Right - width) / 2;
        _status.Frame = new CGRect(x, safe.Top + 8, width, 49);
        _diagnosticLabel.Frame = new CGRect(x, safe.Top + 65, width, 82);
        _panel.Frame = new CGRect(x, bounds.Height - safe.Bottom - 143, width, 133);
        _characterLabel.Frame = new CGRect(14, 11, width - 28, 22);
        _instruction.Frame = new CGRect(14, 36, width - 28, 34);
        nfloat buttonWidth = (width - 40) / 3;
        _importButton.Frame = new CGRect(12, 79, buttonWidth, 42);
        _actionButton.Frame = new CGRect(20 + buttonWidth, 79, buttonWidth, 42);
        _moreButton.Frame = new CGRect(28 + buttonWidth * 2, 79, buttonWidth, 42);
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        _visible = true;
        EnsureCameraAndStart();
    }

    public override void ViewWillDisappear(bool animated)
    {
        _visible = false;
        PauseSession();
        base.ViewWillDisappear(animated);
    }

    public override bool ShouldAutorotate() => true;
    public override UIInterfaceOrientationMask GetSupportedInterfaceOrientations() =>
        UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? UIInterfaceOrientationMask.All : UIInterfaceOrientationMask.AllButUpsideDown;

    private static void ConfigureLabel(UILabel label, nfloat size, nint lines, UIView parent)
    {
        label.Font = UIFont.SystemFontOfSize(size)!;
        label.TextColor = UIColor.White;
        label.TextAlignment = UITextAlignment.Center;
        label.Lines = lines;
        label.Layer.CornerRadius = 12;
        label.ClipsToBounds = true;
        parent.AddSubview(label);
    }

    private void ConfigureButton(UIButton button, string title, Action action)
    {
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(UIColor.White, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold)!;
        button.BackgroundColor = UIColor.FromRGB(57, 74, 116);
        button.Layer.CornerRadius = 12;
        button.TouchUpInside += (_, _) => action();
        _panel.AddSubview(button);
    }

    private async void EnsureCameraAndStart()
    {
        if (_disposed || !_visible || _running || _requestingPermission ||
            UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active) return;
        if (!ARWorldTrackingConfiguration.IsSupported ||
            !ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.Mesh))
        {
            _trackingMessage = "需要带 LiDAR 的 iPhone Pro / iPad Pro";
            _placementMessage = "这台设备暂不能运行空间模式。";
            RefreshStatus();
            return;
        }
        var permission = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (permission == AVAuthorizationStatus.NotDetermined)
        {
            _requestingPermission = true;
            try { await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video); }
            finally { _requestingPermission = false; }
            permission = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        }
        if (_disposed) return;
        if (permission != AVAuthorizationStatus.Authorized)
        {
            _trackingMessage = "请允许相机：更多 → 打开相机设置";
            _placementMessage = "允许后返回应用，画面仅在本机处理。";
            RefreshStatus();
            return;
        }
        if (_visible && UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active) RunSession(false);
    }

    private void RunSession(bool reset)
    {
        StopMotion();
        Interlocked.Increment(ref _sessionGeneration);
        using var configuration = new ARWorldTrackingConfiguration
        {
            PlaneDetection = ARPlaneDetection.Horizontal,
            LightEstimationEnabled = true,
            WorldAlignment = ARWorldAlignment.Gravity,
            SceneReconstruction = ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.MeshWithClassification)
                ? ARSceneReconstruction.MeshWithClassification : ARSceneReconstruction.Mesh
        };
        if (ARWorldTrackingConfiguration.SupportsFrameSemantics(ARFrameSemantics.SmoothedSceneDepth))
            configuration.FrameSemantics |= ARFrameSemantics.SmoothedSceneDepth;
        if (ARWorldTrackingConfiguration.SupportsFrameSemantics(ARFrameSemantics.SceneDepth))
            configuration.FrameSemantics |= ARFrameSemantics.SceneDepth;
        if (reset)
        {
            CancelImport();
            _planes.Clear(); _meshes.Clear(); _planeSurfaces.Clear(); _meshBounds.Clear();
            _characterNode?.RemoveFromParentNode(); _targetVisual?.RemoveFromParentNode();
            _characterAnchor?.Dispose(); _targetAnchor?.Dispose();
            _characterAnchor = null; _targetAnchor = null;
            _characterAnchorNode = null; _characterNode = null; _targetAnchorNode = null; _targetVisual = null;
            _characterPlaneId = null; _targetPlaneId = null;
            _placementMessage = "扫一下地面，点击蓝色区域，让小伙伴出现。";
        }
        _trackingMessage = "正在建立空间追踪…";
        _sceneView.Session.Run(configuration, reset
            ? ARSessionRunOptions.ResetTracking | ARSessionRunOptions.RemoveExistingAnchors : ARSessionRunOptions.None);
        _running = true;
        _sceneView.Playing = true;
        _timer?.Invalidate(); _timer?.Dispose();
        _timer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(0.35), _ => Tick());
        RefreshStatus();
    }

    private void PauseSession()
    {
        if (_disposed) return;
        StopMotion(); CancelImport();
        _running = false;
        Interlocked.Increment(ref _sessionGeneration);
        _timer?.Invalidate(); _timer?.Dispose(); _timer = null;
        _sceneView.Session.Pause(); _sceneView.Playing = false;
        _trackingMessage = "空间追踪已暂停";
        RefreshStatus();
    }

    private void ResetSession()
    {
        if (_visible && !_disposed &&
            ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.Mesh) &&
            AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video) == AVAuthorizationStatus.Authorized)
            RunSession(true);
        else EnsureCameraAndStart();
    }

    private void SelectPlanePoint(UITapGestureRecognizer gesture)
    {
        if (!_running || _moving || gesture.State != UIGestureRecognizerState.Ended) return;
        using var frame = _sceneView.Session.CurrentFrame;
        if (frame?.Camera.TrackingState != ARTrackingState.Normal)
        {
            _placementMessage = "先等追踪稳定，再点击地面。"; RefreshStatus(); return;
        }
        // ARSCNView applies interface rotation and the camera's aspect-fill crop.
        using var query = _sceneView.CreateRaycastQuery(gesture.LocationInView(_sceneView),
            ARRaycastTarget.ExistingPlaneGeometry, ARRaycastTargetAlignment.Horizontal);
        if (query is null) return;
        var hits = _sceneView.Session.Raycast(query);
        try
        {
            var hit = hits.FirstOrDefault(result => result.Anchor is ARPlaneAnchor);
            if (hit is null) { _placementMessage = "这里还没扫描好，请点击蓝色区域。"; RefreshStatus(); return; }
            string planeId = hit.Anchor!.Identifier.AsString();
            if (_characterAnchor is null)
            {
                _characterPlaneId = planeId;
                _characterAnchor = new ARAnchor(SceneDelegate.CharacterAnchorName, hit.WorldTransform);
                _sceneView.Session.AddAnchor(_characterAnchor);
                _placementMessage = "小伙伴正在出现…";
            }
            else
            {
                if (planeId != _characterPlaneId)
                {
                    _placementMessage = "请选小伙伴所在的同一块蓝色平面。"; RefreshStatus(); return;
                }
                ClearTarget();
                _targetPlaneId = planeId;
                _targetAnchor = new ARAnchor(SceneDelegate.TargetAnchorName, hit.WorldTransform);
                _sceneView.Session.AddAnchor(_targetAnchor);
                _placementMessage = "目标已选；角色动作 → 移动到选定点。";
            }
            RefreshStatus();
        }
        finally { foreach (var hit in hits) hit.Dispose(); }
    }

    internal void AttachCharacter(SCNNode anchorNode, string identifier, long epoch)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration) ||
                _characterAnchor?.Identifier.AsString() != identifier) return;
            _characterAnchorNode = anchorNode;
            try { ReplaceCharacterNode(); }
            catch (Exception error)
            {
                _characterNode?.RemoveFromParentNode();
                _characterNode?.Dispose(); _characterNode = null;
                _characterAnchorNode = null;
                if (_characterAnchor is not null)
                {
                    _sceneView.Session.RemoveAnchor(_characterAnchor);
                    _characterAnchor.Dispose(); _characterAnchor = null;
                }
                _characterPlaneId = null;
                ClearTarget();
                _placementMessage = "角色暂时无法显示，可导入其他模型或恢复内置小伙伴。";
                RefreshStatus();
                ShowNotice("角色无法显示", CharacterErrorMessage(error));
                return;
            }
            _placementMessage = "已站在这里。点同一平面选目标，或打开角色动作。";
            RefreshStatus();
        });
    }

    internal void AttachTarget(SCNNode anchorNode, string identifier, long epoch)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration) ||
                _targetAnchor?.Identifier.AsString() != identifier) return;
            _targetAnchorNode = anchorNode;
            using var ring = SCNTorus.Create(0.055f, 0.006f);
            using var material = new SCNMaterial();
            material.Diffuse.Contents = UIColor.FromRGB(255, 198, 83);
            material.LightingModelName = SCNLightingModel.Constant;
            ring.Materials = new[] { material };
            _targetVisual = SCNNode.FromGeometry(ring);
            _targetVisual.Position = new SCNVector3(0, 0.012f, 0);
            anchorNode.AddChildNode(_targetVisual);
            RefreshStatus();
        });
    }

    private void ReplaceCharacterNode(CharacterAsset? candidateAsset = null)
    {
        var asset = candidateAsset ?? _asset;
        // Validate even an unplaced model now, so a later AR callback cannot
        // encounter malformed flat/empty geometry for the first time.
        var replacement = asset.CreateNode(_height);
        bool transferred = false;
        var previousNode = _characterNode;
        try
        {
            if (_characterAnchorNode is null) return;
            StopMotion();
            replacement.Position = previousNode?.Position ?? new SCNVector3(0, 0, 0);
            replacement.EulerAngles = previousNode?.EulerAngles ?? new SCNVector3(0, 0, 0);
            CharacterMotion.Play(asset, replacement, "idle");
            // Keep the old node attached until the candidate has been attached
            // successfully. No asset ownership changes occur in this method.
            _characterAnchorNode.AddChildNode(replacement);
            previousNode?.RemoveFromParentNode();
            _characterNode = replacement;
            transferred = true;
        }
        catch
        {
            replacement.RemoveFromParentNode();
            if (previousNode is not null && previousNode.ParentNode is null)
                _characterAnchorNode?.AddChildNode(previousNode);
            _characterNode = previousNode;
            throw;
        }
        finally
        {
            if (!transferred) replacement.Dispose();
        }
        previousNode?.Dispose();
    }

    private void ClearTarget()
    {
        _targetVisual?.RemoveFromParentNode();
        _targetVisual = null; _targetAnchorNode = null; _targetPlaneId = null;
        if (_targetAnchor is not null)
        {
            _sceneView.Session.RemoveAnchor(_targetAnchor);
            _targetAnchor.Dispose(); _targetAnchor = null;
        }
    }

    private void ShowActions()
    {
        if (_characterNode is null) { ShowNotice("先让小伙伴出现", "缓慢扫描地面，点击蓝色区域放置角色。"); return; }
        var sheet = UIAlertController.Create("角色动作", _asset.DisplayName + " · 手动触发", UIAlertControllerStyle.ActionSheet);
        if (_asset.SupportsWave) sheet.AddAction(UIAlertAction.Create("挥手", UIAlertActionStyle.Default, _ => PlayPose("wave")));
        sheet.AddAction(UIAlertAction.Create("看向我", UIAlertActionStyle.Default, _ => LookAtUser()));
        sheet.AddAction(UIAlertAction.Create("待机", UIAlertActionStyle.Default, _ => PlayPose("idle")));
        var move = UIAlertAction.Create("移动到选定点（最多 1.5 米）", UIAlertActionStyle.Default, _ => MoveToTarget());
        move.Enabled = _targetAnchorNode is not null && !_moving;
        sheet.AddAction(move);
        foreach (string clip in _asset.AnimationNames.Take(8))
        {
            string name = clip;
            sheet.AddAction(UIAlertAction.Create("播放：" + name, UIAlertActionStyle.Default, _ =>
            {
                var confirm = UIAlertController.Create("播放模型动画",
                    name + "\n这个动画可能自带位移，不受地面路线检查；本次只播放一次。",
                    UIAlertControllerStyle.Alert);
                confirm.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
                confirm.AddAction(UIAlertAction.Create("播放一次", UIAlertActionStyle.Default, _ =>
                {
                    StopMotion();
                    if (_characterNode is not null && !CharacterMotion.PlayEmbeddedAnimation(_asset, _characterNode, name))
                        ShowNotice("无法播放这个动作", "模型里的动画没有成功加载。");
                    else { _placementMessage = "正在播放模型动画：" + name; RefreshStatus(); }
                }));
                PresentViewController(confirm, true, null);
            }));
        }
        sheet.AddAction(UIAlertAction.Create("停止动作", UIAlertActionStyle.Default, _ =>
        {
            StopMotion(); _placementMessage = "动作已停止。再点地面可选目标。"; RefreshStatus();
        }));
        PresentSheet(sheet, _actionButton);
    }

    private void PlayPose(string pose)
    {
        StopMotion();
        if (_characterNode is not null && !CharacterMotion.Play(_asset, _characterNode, pose))
            ShowNotice("模型没有这个动作", "导入模型只播放自身带有的动画；它仍可以进行空间平移。");
        else
        {
            _placementMessage = pose == "wave" ? "小伙伴正在向你挥手。" : "小伙伴正在待机。";
            RefreshStatus();
        }
    }

    private void LookAtUser()
    {
        StopMotion();
        if (_characterNode is null || _characterAnchorNode is null || !TrackingNormal()) return;
        var cameraNode = _sceneView.PointOfView;
        if (cameraNode is null) return;
        var camera = _characterAnchorNode.ConvertPositionFromNode(cameraNode.WorldPosition, null);
        var delta = camera - _characterNode.Position;
        _characterNode.EulerAngles = new SCNVector3(0, MathF.Atan2(delta.X, delta.Z), 0);
        _placementMessage = "小伙伴正看向你。"; RefreshStatus();
    }

    private void MoveToTarget()
    {
        if (_characterNode is null || _characterAnchorNode is null || _targetAnchorNode is null) return;
        var start = _characterNode.PresentationNode.WorldPosition;
        var target = _targetAnchorNode.WorldPosition;
        if (!CanMove(start, target, out string reason)) { ShowNotice("暂时不能走过去", reason); return; }
        StopMotion();
        int motion = ++_motionGeneration;
        long epoch = Interlocked.Read(ref _sessionGeneration);
        _moving = true;
        var localTarget = _characterAnchorNode.ConvertPositionFromNode(target, null);
        var delta = localTarget - _characterNode.Position;
        _characterNode.EulerAngles = new SCNVector3(0, MathF.Atan2(delta.X, delta.Z), 0);
        bool hasWalk = CharacterMotion.Play(_asset, _characterNode, "walk");
        double duration = Math.Max(0.6, (target - start).Length / 0.28);
        var action = SCNAction.MoveTo(localTarget, duration);
        _characterNode.RunAction(action, MoveActionKey, () => BeginInvokeOnMainThread(() =>
        {
            if (_disposed || motion != _motionGeneration || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            _moving = false;
            _characterNode?.RemoveAction(MoveActionKey);
            if (_characterNode is not null) CharacterMotion.Play(_asset, _characterNode, "idle");
            ClearTarget();
            _placementMessage = "已到达目标。再点地面可选下一处。"; RefreshStatus();
        }));
        _placementMessage = hasWalk ? "正在走过去…" : "正在空间平移（这个模型没有行走动画）…";
        RefreshStatus();
    }

    private bool CanMove(SCNVector3 start, SCNVector3 target, out string reason)
    {
        reason = "";
        if (!TrackingNormal()) { reason = "空间追踪还不稳定，请放慢手机。"; return false; }
        if (_characterPlaneId is null || _targetPlaneId != _characterPlaneId ||
            !_planeSurfaces.TryGetValue(_characterPlaneId, out var plane))
        { reason = "只支持在同一块已扫描的水平面上移动。"; return false; }
        if (MathF.Abs(start.Y - target.Y) > 0.055f || (target - start).Length > 1.5f)
        { reason = "目标需与角色等高，距离不超过 1.5 米。"; return false; }
        float radius = MathF.Max(0.09f, _height * 0.42f);
        var minimum = new SCNVector3(); var maximum = new SCNVector3();
        if (_characterNode?.GetBoundingBox(ref minimum, ref maximum) == true)
        {
            float x = MathF.Max(MathF.Abs(minimum.X), MathF.Abs(maximum.X));
            float z = MathF.Max(MathF.Abs(minimum.Z), MathF.Abs(maximum.Z));
            radius = MathF.Max(radius, MathF.Sqrt(x * x + z * z) + 0.02f);
        }
        if (!float.IsFinite(radius))
        { reason = "模型尺寸异常，暂不能进行路线移动。"; return false; }
        int steps = Math.Max(1, (int)Math.Ceiling((target - start).Length / 0.07));
        for (int step = 0; step <= steps; step++)
        {
            var point = start + (target - start) * (step / (float)steps);
            if (!plane.ContainsWithMargin(point, radius))
            { reason = "路线靠近扫描边界，请选更靠里面的目标，或继续扫描。"; return false; }
        }
        if (_meshes.Count == 0 || _meshBounds.Count != _meshes.Count || _meshBounds.Values.Any(value => value.Length == 0))
        { reason = "LiDAR 网格还不完整，请扫描角色与目标之间的区域。"; return false; }
        foreach (var bounds in _meshBounds.Values.SelectMany(value => value))
        {
            // Triangle AABBs intersecting the swept character corridor block
            // movement. The floor is below the foot margin. This conservative
            // check includes thin vertical triangles; it is not pathfinding.
            if (bounds.Max.Y < start.Y + 0.045f || bounds.Min.Y > start.Y + _height + 0.08f) continue;
            if (SegmentTouchesBox(start, target, bounds.Min, bounds.Max, radius))
            { reason = "LiDAR 检测到路线可能被家具或墙壁挡住，请换个目标。"; return false; }
        }
        return true;
    }

    private static bool SegmentTouchesBox(SCNVector3 a, SCNVector3 b, SCNVector3 min, SCNVector3 max, float radius)
    {
        float low = 0, high = 1;
        return Clip(a.X, b.X - a.X, min.X - radius, max.X + radius, ref low, ref high) &&
               Clip(a.Z, b.Z - a.Z, min.Z - radius, max.Z + radius, ref low, ref high);
    }

    private static bool Clip(float origin, float direction, float min, float max, ref float low, ref float high)
    {
        if (MathF.Abs(direction) < 0.00001f) return origin >= min && origin <= max;
        float left = (min - origin) / direction, right = (max - origin) / direction;
        if (left > right) (left, right) = (right, left);
        low = MathF.Max(low, left); high = MathF.Min(high, right);
        return low <= high;
    }

    private bool TrackingNormal()
    {
        if (!_running) return false;
        using var frame = _sceneView.Session.CurrentFrame;
        return frame?.Camera.TrackingState == ARTrackingState.Normal;
    }

    private void StopMotion()
    {
        ++_motionGeneration;
        bool wasMoving = _moving;
        _moving = false;
        if (_characterNode is not null)
        {
            if (wasMoving) _characterNode.Position = _characterNode.PresentationNode.Position;
            CharacterMotion.Stop(_characterNode);
            _characterNode.RemoveAllActions();
        }
    }

    private void Tick()
    {
        if (!_running || _disposed) return;
        if (_moving && _characterNode is not null && _targetAnchorNode is not null &&
            !CanMove(_characterNode.PresentationNode.WorldPosition, _targetAnchorNode.WorldPosition, out string reason))
        {
            StopMotion(); _placementMessage = "已停下：" + reason; RefreshStatus();
        }
        if (!_showDebug) return;
        using var frame = _sceneView.Session.CurrentFrame;
        if (frame is null) { _diagnosticLabel.Text = "正在等待相机帧…"; return; }
        try { _diagnosticLabel.Text = LidarDiagnostics.Analyze(frame, _meshes.Count); }
        catch (Exception) { _diagnosticLabel.Text = "诊断数据暂不可用。"; }
    }

    private void ShowMore()
    {
        var sheet = UIAlertController.Create("Alice · 空间伙伴", "0.1.0 · 本地 LiDAR 体验", UIAlertControllerStyle.ActionSheet);
        sheet.AddAction(UIAlertAction.Create("重新扫描", UIAlertActionStyle.Default, _ => ResetSession()));
        sheet.AddAction(UIAlertAction.Create(_showDebug ? "隐藏调试数据" : "显示调试数据", UIAlertActionStyle.Default, _ =>
        {
            _showDebug = !_showDebug; _diagnosticLabel.Hidden = !_showDebug;
            _sceneView.DebugOptions = _showDebug ? ARSCNDebugOptions.ShowFeaturePoints : SCNDebugOptions.None; Tick();
        }));
        sheet.AddAction(UIAlertAction.Create("调整角色大小", UIAlertActionStyle.Default, _ => ChangeHeight()));
        sheet.AddAction(UIAlertAction.Create("角色转身 90°", UIAlertActionStyle.Default, _ =>
        {
            StopMotion();
            if (_characterNode is not null) _characterNode.EulerAngles += new SCNVector3(0, MathF.PI / 2, 0);
        }));
        sheet.AddAction(UIAlertAction.Create("恢复内置小伙伴", UIAlertActionStyle.Default, _ =>
        {
            CancelImport();
            CharacterAsset? candidate = null;
            try
            {
                candidate = CharacterFactory.CreateDefault();
                ReplaceCharacterNode(candidate);
                var previous = _asset; _asset = candidate; candidate = null;
                previous.Dispose(); RefreshStatus();
            }
            catch (Exception error) { ShowNotice("角色没有更换", CharacterErrorMessage(error)); }
            finally { candidate?.Dispose(); }
        }));
        sheet.AddAction(UIAlertAction.Create("打开相机设置", UIAlertActionStyle.Default, _ =>
        {
            using var url = new NSUrl(UIApplication.OpenSettingsUrlString);
            UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
        }));
        sheet.AddAction(UIAlertAction.Create("使用帮助 / 模型格式", UIAlertActionStyle.Default, _ => ShowNotice("让小伙伴进入房间",
            "1. 缓慢扫描地面，点击蓝色区域放置。\n2. 再点同一平面选目标，在角色动作中确认移动。\n\n支持单文件 USDZ / DAE / OBJ / GLB / VRM，最多 80 MB；推荐含贴图的 USDZ。GLB / VRM 目前为基础静态展示，不含骨骼动画、表情或弹簧物理。外部纹理依赖不会自动打包。其他模型动画需要文件本身带有。\n\n移动只检查已扫描的同一平面与 LiDAR 网格，最多 1.5 米，会保守停下；目前没有寻路和 AI。画面仅在本机处理。")));
        PresentSheet(sheet, _moreButton);
    }

    private void ChangeHeight()
    {
        var alert = UIAlertController.Create("角色高度", "输入米数（0.15～0.8），默认 0.35。", UIAlertControllerStyle.Alert);
        alert.AddTextField(field => { field.Text = _height.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture); field.KeyboardType = UIKeyboardType.DecimalPad; });
        alert.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("调整", UIAlertActionStyle.Default, _ =>
        {
            string value = alert.TextFields![0].Text?.Replace(',', '.') ?? "";
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float height) ||
                !float.IsFinite(height) || height < 0.15f || height > 0.8f)
            { ShowNotice("高度不合适", "请输入 0.15 到 0.8 之间的米数。"); return; }
            float previousHeight = _height;
            _height = height;
            try { ReplaceCharacterNode(); RefreshStatus(); }
            catch (Exception error)
            {
                _height = previousHeight;
                RefreshStatus();
                ShowNotice("大小没有调整", CharacterErrorMessage(error));
            }
        }));
        PresentViewController(alert, true, null);
    }

    private void ImportCharacter()
    {
        StopMotion(); CancelImport();
#pragma warning disable CA1422
        var picker = new UIDocumentPickerViewController(new[] {
            "com.pixar.universal-scene-description-mobile", "com.collada.digital-asset-exchange",
            "public.3d-content", "public.item"
        }, UIDocumentPickerMode.Open) { AllowsMultipleSelection = false };
#pragma warning restore CA1422
        picker.ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
        picker.DidPickDocumentAtUrls += (_, args) =>
        {
            var url = args.Urls.FirstOrDefault();
            DismissViewController(true, () =>
            {
                _visible = true; EnsureCameraAndStart();
                if (url is not null) CopyAndLoadCharacter(url);
            });
        };
        picker.WasCancelled += (_, _) => DismissViewController(true, () => { _visible = true; EnsureCameraAndStart(); });
        PresentViewController(picker, true, null);
    }

    private async void CopyAndLoadCharacter(NSUrl url)
    {
        CancelImport();
        int operation = ++_importGeneration;
        long epoch = Interlocked.Read(ref _sessionGeneration);
        _importCancellation = new CancellationTokenSource();
        var cancellation = _importCancellation.Token;
        bool scoped = url.StartAccessingSecurityScopedResource();
        string? destination = null;
        bool accepted = false;
        CharacterAsset? rejectedAsset = null;
        _placementMessage = "正在读取角色文件…"; RefreshStatus();
        try
        {
            string source = url.Path ?? throw new InvalidOperationException("没有获得本地文件路径。");
            string extension = Path.GetExtension(source).ToLowerInvariant();
            if (!CharacterImporter.AllowedExtensions.Contains(extension))
                throw new InvalidOperationException("请选择 USDZ、DAE、OBJ、GLB 或 VRM 文件。");
            if (new FileInfo(source).Length > MaximumModelBytes)
                throw new InvalidOperationException("文件超过 80 MB，请先压缩模型和贴图。");
            string filename = new string(Path.GetFileNameWithoutExtension(source)
                .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').Take(64).ToArray());
            if (filename.Length == 0) filename = "Character";
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Characters");
            Directory.CreateDirectory(directory);
            directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            destination = Path.Combine(directory, filename + extension);
            await Task.Run(async () =>
            {
                await using var input = File.OpenRead(source);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) != 0)
                {
                    total += read;
                    if (total > MaximumModelBytes) throw new InvalidOperationException("文件超过 80 MB。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_disposed || operation != _importGeneration || epoch != Interlocked.Read(ref _sessionGeneration))
                throw new OperationCanceledException();
            // SceneKit node creation and attachment stay on the UIKit main thread.
            rejectedAsset = CharacterImporter.Load(destination);
            ReplaceCharacterNode(rejectedAsset);
            var previous = _asset; _asset = rejectedAsset; rejectedAsset = null;
            accepted = true;
            previous.Dispose();
            _placementMessage = _characterNode is null ? "角色已准备好，点击蓝色区域放置。" : "角色已换好；可以打开角色动作。";
            RefreshStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_disposed && operation == _importGeneration)
            {
                _placementMessage = "导入未完成，原来的小伙伴已保留。";
                RefreshStatus();
                ShowNotice("没有导入成功", CharacterErrorMessage(error));
            }
        }
        finally
        {
            rejectedAsset?.Dispose();
            if (scoped) url.StopAccessingSecurityScopedResource();
            if (!accepted && destination is not null)
            {
                try { File.Delete(destination); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (operation == _importGeneration) { _importCancellation?.Dispose(); _importCancellation = null; }
        }
    }

    private void CancelImport()
    {
        ++_importGeneration;
        _importCancellation?.Cancel(); _importCancellation?.Dispose(); _importCancellation = null;
    }

    private void PresentSheet(UIAlertController sheet, UIView source)
    {
        sheet.AddAction(UIAlertAction.Create("取消", UIAlertActionStyle.Cancel, null));
        if (sheet.PopoverPresentationController is { } popover) { popover.SourceView = source; popover.SourceRect = source.Bounds; }
        PresentViewController(sheet, true, null);
    }

    private void ShowNotice(string title, string message)
    {
        if (_disposed) return;
        var notice = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
        notice.AddAction(UIAlertAction.Create("知道了", UIAlertActionStyle.Default, null));
        PresentViewController(notice, true, null);
    }

    private static string CharacterErrorMessage(Exception error) =>
        error is InvalidOperationException or InvalidDataException or NotSupportedException ? error.Message :
        "模型暂时无法显示。推荐转换为自包含 USDZ，或换一个模型后重试。";

    internal void UpdateTracking(ARCamera camera, long epoch)
    {
        var state = camera.TrackingState; var reason = camera.TrackingStateReason;
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            if (state != ARTrackingState.Normal) StopMotion();
            _trackingMessage = state switch
            {
                ARTrackingState.Normal => "空间追踪稳定 · LiDAR 已接入",
                ARTrackingState.NotAvailable => "空间追踪暂不可用",
                _ => reason switch
                {
                    ARTrackingStateReason.ExcessiveMotion => "移动过快，请放慢手机",
                    ARTrackingStateReason.InsufficientFeatures => "对准有纹理的地面，增加光线",
                    ARTrackingStateReason.Relocalizing => "正在重新定位，请对准之前扫描的区域",
                    _ => "缓慢移动手机，扫描地面"
                }
            };
            RefreshStatus();
        });
    }

    internal void UpdatePlane(string identifier, bool remove, long epoch, SCNVector3[]? boundary = null, SCNMatrix4? transform = null)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            if (remove)
            {
                _planes.Remove(identifier); _planeSurfaces.Remove(identifier);
                if (_characterPlaneId == identifier || _targetPlaneId == identifier)
                {
                    StopMotion();
                }
            }
            else
            {
                _planes.Add(identifier);
                if (boundary is not null && transform is { } matrix)
                    _planeSurfaces[identifier] = new PlaneSurface(boundary.Select(point => SCNVector3.TransformPosition(point, matrix)).ToArray());
            }
            TryRebindLostPlane();
            RefreshStatus();
        });
    }

    private void TryRebindLostPlane()
    {
        // ARKit may merge/remove a plane while preserving world anchors. Adopt a
        // replacement only when exactly one current real polygon supports both
        // the existing character and target at the same height.
        if (_characterPlaneId is null || _planes.Contains(_characterPlaneId) || _characterNode is null) return;
        var character = _characterNode.WorldPosition;
        var candidates = _planeSurfaces.Where(pair =>
            pair.Value.ContainsWithMargin(character, 0.09f) &&
            (_targetAnchorNode is null || pair.Value.ContainsWithMargin(_targetAnchorNode.WorldPosition, 0.09f))).Take(2).ToArray();
        if (candidates.Length != 1) return;
        string previous = _characterPlaneId;
        _characterPlaneId = candidates[0].Key;
        if (_targetPlaneId == previous) _targetPlaneId = _characterPlaneId;
    }

    internal void UpdateMesh(string identifier, bool remove, long epoch, MeshBounds[]? bounds = null)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            if (remove) { _meshes.Remove(identifier); _meshBounds.Remove(identifier); }
            else { _meshes.Add(identifier); _meshBounds[identifier] = bounds ?? Array.Empty<MeshBounds>(); }
        });
    }

    internal void AnchorRemoved(string identifier, long epoch)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            if (_characterAnchor?.Identifier.AsString() == identifier)
            {
                StopMotion(); _characterNode = null; _characterAnchorNode = null;
                _characterAnchor?.Dispose(); _characterAnchor = null; _characterPlaneId = null;
                ClearTarget(); _placementMessage = "角色锚点已丢失，请重新点击蓝色区域放置。";
            }
            else if (_targetAnchor?.Identifier.AsString() == identifier)
            {
                StopMotion(); _targetVisual = null; _targetAnchorNode = null;
                _targetAnchor?.Dispose(); _targetAnchor = null; _targetPlaneId = null;
            }
            RefreshStatus();
        });
    }

    internal void Interrupted(bool ended, long epoch)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            StopMotion(); CancelImport();
            _trackingMessage = ended ? "中断结束，正在重新定位…" : "相机被系统中断";
            RefreshStatus();
            if (ended && _visible) RunSession(false);
        });
    }

    internal void SessionFailed(NSError error, long epoch)
    {
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            PauseSession(); _trackingMessage = "空间追踪启动失败";
            _placementMessage = "更多 → 重新扫描可重试。"; RefreshStatus();
        });
    }

    private void RefreshStatus()
    {
        _status.Text = "Alice · 空间伙伴\n" + _trackingMessage;
        _characterLabel.Text = _asset.DisplayName + $" · {_height:0.00} 米";
        _instruction.Text = _placementMessage;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            PauseSession(); _disposed = true; _visible = false;
            _backgroundObserver?.Dispose(); _activeObserver?.Dispose();
            _sceneView.Delegate = null; _sceneDelegate?.Dispose();
            _characterAnchor?.Dispose(); _targetAnchor?.Dispose(); _asset.Dispose();
        }
        base.Dispose(disposing);
    }

    internal readonly record struct MeshBounds(SCNVector3 Min, SCNVector3 Max);

    private sealed class PlaneSurface(SCNVector3[] boundary)
    {
        public bool ContainsWithMargin(SCNVector3 point, float radius)
        {
            if (boundary.Length < 3 || !float.IsFinite(radius) || radius < 0 ||
                !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                MathF.Abs(point.Y - boundary[0].Y) > 0.055f) return false;
            bool inside = false;
            double squaredRadius = (double)radius * radius;
            // The center must be in the real polygon and the entire foot circle
            // must clear every edge. Eight discrete circle samples miss corners.
            for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
            {
                var a = boundary[i]; var b = boundary[j];
                if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(a.Z) ||
                    !float.IsFinite(b.X) || !float.IsFinite(b.Z)) return false;
                if ((a.Z > point.Z) != (b.Z > point.Z) &&
                    point.X < ((double)b.X - a.X) * (point.Z - a.Z) / ((double)b.Z - a.Z) + a.X)
                    inside = !inside;
                double dx = (double)b.X - a.X, dz = (double)b.Z - a.Z;
                double lengthSquared = dx * dx + dz * dz;
                double t = lengthSquared > 0 ? Math.Clamp(((point.X - a.X) * dx + (point.Z - a.Z) * dz) / lengthSquared, 0, 1) : 0;
                double x = point.X - (a.X + t * dx), z = point.Z - (a.Z + t * dz);
                if (x * x + z * z < squaredRadius) return false;
            }
            return inside;
        }
    }
}
