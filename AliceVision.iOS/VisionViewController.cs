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

/// <summary>LiDAR companion with local characters and explicitly enabled autonomy.</summary>
public sealed partial class VisionViewController : UIViewController
{
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
        ConfigureCompanionControls();
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
        _status.Frame = new CGRect(x, safe.Top + 8, width - 83, 49);
        _diagnosticLabel.Frame = new CGRect(x, safe.Top + 142, width, 82);
        _panel.Frame = new CGRect(x, bounds.Height - safe.Bottom - 143, width, 133);
        _characterLabel.Frame = new CGRect(14, 11, width - 28, 22);
        _instruction.Frame = new CGRect(14, 36, width - 28, 34);
        nfloat buttonWidth = (width - 48) / 4;
        _importButton.Frame = new CGRect(12, 79, buttonWidth, 42);
        _actionButton.Frame = new CGRect(20 + buttonWidth, 79, buttonWidth, 42);
        _aiButton.Frame = new CGRect(28 + buttonWidth * 2, 79, buttonWidth, 42);
        _moreButton.Frame = new CGRect(36 + buttonWidth * 3, 79, buttonWidth, 42);
        LayoutCompanionControls(x, safe.Top, width);
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
        StopAutonomy("空间会话改变，AI 已暂停。");
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
            ClearActivityZones();
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
        StopAutonomy("应用离开前台，AI 已暂停。");
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
            if (HandleZoneTap(hit, planeId)) return;
            StopAutonomy("手动选点，AI 已暂停。");
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
        StopAutonomy("角色变化，AI 已暂停。");
        ++_assetGeneration;
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
            _requiresPoseReset = false;
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
            if (!transferred) CharacterAsset.DisposeNode(replacement);
        }
        if (previousNode is not null) CharacterAsset.DisposeNode(previousNode);
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
        StopAutonomy("手动控制，AI 已暂停。"); StopMotion();
        if (_characterNode is null) { ShowNotice("先让小伙伴出现", "缓慢扫描地面，点击蓝色区域放置角色。"); return; }
        var sheet = UIAlertController.Create("角色动作", _asset.DisplayName + " · 手动触发", UIAlertControllerStyle.ActionSheet);
        foreach (string action in _asset.AvailableActions.Where(action => action is not "idle" and not "walk"))
        {
            string id = action;
            sheet.AddAction(UIAlertAction.Create(ActionLabel(id), UIAlertActionStyle.Default, _ => PlayPose(id)));
        }
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
                    _requiresPoseReset = true;
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
            ShowNotice("模型没有这个动作", "没有找到这个动作需要的人形骨骼映射。请使用标准 VRM 人形绑定；模型仍可显示与空间平移。");
        else
        {
            _placementMessage = "小伙伴正在" + ActionLabel(pose) + "。";
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
        TryMoveToWorldTarget(_targetAnchorNode.WorldPosition, false, out string reason);
        if (!string.IsNullOrEmpty(reason)) ShowNotice("暂时不能走过去", reason);
    }

    private bool TryMoveToWorldTarget(SCNVector3 target, bool autonomous, out string reason)
    {
        reason = "";
        if (_characterNode is null || _characterAnchorNode is null) { reason = "角色还未放置。"; return false; }
        if (_requiresPoseReset)
        {
            try { ReplaceCharacterNode(); }
            catch (Exception) { reason = "请先恢复角色姿势，再尝试路线移动。"; return false; }
            if (_characterNode is null) { reason = "角色还未放置。"; return false; }
        }
        var start = _characterNode.PresentationNode.WorldPosition;
        if (!CanMove(start, target, out reason, autonomous)) return false;
        StopMotion();
        int motion = ++_motionGeneration;
        long epoch = Interlocked.Read(ref _sessionGeneration);
        _moving = true;
        _movingDestination = target;
        _movingAutonomously = autonomous;
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
            _movingDestination = null;
            _characterNode?.RemoveAction(MoveActionKey);
            if (_characterNode is not null) CharacterMotion.Play(_asset, _characterNode, "idle");
            ClearTarget();
            _placementMessage = "已到达目标。再点地面可选下一处。"; RefreshStatus();
        }));
        _placementMessage = hasWalk ? "正在走过去…" : "正在空间平移（这个模型没有行走动画）…";
        RefreshStatus();
        return true;
    }

    private bool CanMove(SCNVector3 start, SCNVector3 target, out string reason, bool autonomous = false)
    {
        reason = "";
        if (!TrackingNormal()) { reason = "空间追踪还不稳定，请放慢手机。"; return false; }
        if (_characterPlaneId is null || (!autonomous && _targetPlaneId != _characterPlaneId) ||
            !_planeSurfaces.TryGetValue(_characterPlaneId, out var plane))
        { reason = "只支持在同一块已扫描的水平面上移动。"; return false; }
        if (MathF.Abs(start.Y - target.Y) > 0.055f || (target - start).Length > 1.5f)
        { reason = "目标需与角色等高，距离不超过 1.5 米。"; return false; }
        float radius = CharacterFootRadius();
        if (!float.IsFinite(radius))
        { reason = "模型尺寸异常，暂不能进行路线移动。"; return false; }
        if (!ActivitySegmentAllowed(start, target, radius, autonomous, out reason)) return false;
        if (!plane.SegmentWithMargin(start, target, radius))
        { reason = "路线靠近扫描边界，请选更靠里面的目标，或继续扫描。"; return false; }
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
        _movingDestination = null;
        _movingAutonomously = false;
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
        if (_moving && _characterNode is not null && _movingDestination is { } destination &&
            !CanMove(_characterNode.PresentationNode.WorldPosition, destination, out string reason, _movingAutonomously))
        {
            StopMotion(); _placementMessage = "已停下：" + reason; RefreshStatus();
            StopAutonomy("路线条件改变，AI 已暂停。");
        }
        AutonomyTick();
        if (!_showDebug) return;
        using var frame = _sceneView.Session.CurrentFrame;
        if (frame is null) { _diagnosticLabel.Text = "正在等待相机帧…"; return; }
        try { _diagnosticLabel.Text = LidarDiagnostics.Analyze(frame, _meshes.Count); }
        catch (Exception) { _diagnosticLabel.Text = "诊断数据暂不可用。"; }
    }

    private void ShowMore()
    {
        StopAutonomy("打开更多控制，AI 已暂停。"); StopMotion();
        var sheet = UIAlertController.Create("Alice · 空间伙伴", "0.1.0 · 本地 LiDAR 体验", UIAlertControllerStyle.ActionSheet);
        sheet.AddAction(UIAlertAction.Create("AI 模型与 API 设置", UIAlertActionStyle.Default, _ => OpenAiSettings()));
        sheet.AddAction(UIAlertAction.Create("画活动区域（绿色）", UIAlertActionStyle.Default, _ => BeginZoneEditing(false)));
        sheet.AddAction(UIAlertAction.Create("添加禁止区域（红色）", UIAlertActionStyle.Default, _ => BeginZoneEditing(true)));
        sheet.AddAction(UIAlertAction.Create("清除活动 / 禁止区域", UIAlertActionStyle.Destructive, _ => ClearActivityZones()));
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
            "1. 扫描地面，点击蓝色区域放置。\n2. 更多 → 画活动区域；可添加红色禁区。\n3. 配置支持图片的 API，明确启动 AI 后，它会周期观察并自行选择动作。红色急停随时终止。\n\n可自行导入单文件 USDZ / DAE / OBJ / GLB / VRM，最大 1 GiB；复杂模型仍可能超出手机内存。具备标准人形骨骼映射的 VRM 可使用识别到的骨骼动作；其他模型仍可显示，只开放可靠识别的动作/内置动画。外部贴图不会自动打包。\n\n移动只检查同一已扫描平面和 LiDAR 网格，每次最多 1.5 米；这是保守限位，不是完整寻路。AI 开启时会向你选择的 API 发送当前视图（含角色）；关闭、后台、重定位、改区域或导入模型即暂停，返回后不会自动恢复上传。")));
        PresentSheet(sheet, _moreButton);
    }

    private void ChangeHeight()
    {
        StopAutonomy("正在调整大小，AI 已暂停。"); StopMotion();
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
        StopAutonomy("正在导入模型，AI 已暂停。");
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
        _importButton.Enabled = false;
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
            if (new FileInfo(source).Length > CharacterImporter.MaximumFileBytes)
                throw new InvalidOperationException("文件超过 1 GiB，请先压缩模型和贴图。");
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
                    if (total > CharacterImporter.MaximumFileBytes) throw new InvalidOperationException("文件超过 1 GiB。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            }, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_disposed || operation != _importGeneration || epoch != Interlocked.Read(ref _sessionGeneration))
                throw new OperationCanceledException();
            // SceneKit node creation and attachment stay on the UIKit main thread.
            rejectedAsset = await CharacterImporter.LoadAsync(destination, cancellation, message =>
            {
                if (_disposed || operation != _importGeneration) return;
                _placementMessage = message;
                RefreshStatus();
            });
            cancellation.ThrowIfCancellationRequested();
            if (_disposed || operation != _importGeneration || epoch != Interlocked.Read(ref _sessionGeneration))
                throw new OperationCanceledException();
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
            if (!_disposed && _importCancellation is null) _importButton.Enabled = true;
        }
    }

    private void CancelImport()
    {
        ++_importGeneration;
        _importCancellation?.Cancel(); _importCancellation?.Dispose(); _importCancellation = null;
        if (!_disposed) _importButton.Enabled = true;
    }

    public override void DidReceiveMemoryWarning()
    {
        base.DidReceiveMemoryWarning();
        if (_disposed) return;
        StopAutonomy("系统内存紧张，AI 已暂停。");
        StopMotion(); CancelImport();
        // iOS can reduce the app's headroom after a successful import. Release
        // the high-resolution asset, never silently replace its textures with
        // low-resolution copies. The user's source/copy remains on disk.
        if (!_asset.IsBuiltIn)
        {
            if (_characterNode is not null) CharacterAsset.DisposeNode(_characterNode);
            _characterNode = null;
            if (_characterAnchor is not null)
            {
                _sceneView.Session.RemoveAnchor(_characterAnchor);
                _characterAnchor.Dispose(); _characterAnchor = null;
            }
            _characterAnchorNode = null; _characterPlaneId = null;
            ClearTarget();
            var previous = _asset;
            _asset = CharacterFactory.CreateDefault();
            previous.Dispose();
            ++_assetGeneration;
        }
        _placementMessage = "系统内存警告：已停止导入并卸载大模型保护内存；原文件未修改，没有降低画质。可重启应用后重试。";
        RefreshStatus();
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
        error is OutOfMemoryException ? "手机内存不足，已停止原画质导入；没有缩小贴图或修改源文件。请重启应用后重试，超出设备容量的模型仍无法加载。" :
        error is InvalidOperationException or InvalidDataException or NotSupportedException ? error.Message :
        "模型暂时无法显示。推荐转换为自包含 USDZ，或换一个模型后重试。";

    internal void UpdateTracking(ARCamera camera, long epoch)
    {
        var state = camera.TrackingState; var reason = camera.TrackingStateReason;
        BeginInvokeOnMainThread(() =>
        {
            if (_disposed || !_running || epoch != Interlocked.Read(ref _sessionGeneration)) return;
            if (state != ARTrackingState.Normal) { StopMotion(); StopAutonomy("追踪不稳定，AI 已暂停。"); }
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
                if (_activityArea?.PlaneId == identifier) ClearActivityZones();
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
                StopAutonomy("角色锚点丢失，AI 已暂停。");
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
            StopAutonomy("相机被中断，AI 已暂停。"); StopMotion(); CancelImport();
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
        RefreshCompanionStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            PauseSession(); _disposed = true; _visible = false;
            DisposeCompanionControls();
            _backgroundObserver?.Dispose(); _activeObserver?.Dispose();
            _sceneView.Delegate = null; _sceneDelegate?.Dispose();
            _characterAnchor?.Dispose(); _targetAnchor?.Dispose(); _asset.Dispose();
        }
        base.Dispose(disposing);
    }

    internal readonly record struct MeshBounds(SCNVector3 Min, SCNVector3 Max);

    private sealed class PlaneSurface(SCNVector3[] boundary)
    {
        private readonly System.Numerics.Vector2[] _boundary2D = boundary.Select(point =>
            new System.Numerics.Vector2(point.X, point.Z)).ToArray();

        public bool SegmentWithMargin(SCNVector3 start, SCNVector3 end, float radius) =>
            ContainsWithMargin(start, radius) && ContainsWithMargin(end, radius) &&
            AliceVision.Core.Spatial.ActivityArea.SegmentInsideSupportBoundary(_boundary2D,
                new System.Numerics.Vector2(start.X, start.Z), new System.Numerics.Vector2(end.X, end.Z), radius);

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
