using System;
using System.Collections.Generic;
using System.Threading;
using ARKit;
using AVFoundation;
using CoreGraphics;
using Foundation;
using SceneKit;
using UIKit;
using nfloat = System.Runtime.InteropServices.NFloat;

namespace AliceVision.iOS;

public sealed class VisionViewController : UIViewController
{
    private readonly ARSCNView _sceneView = new();
    private readonly UILabel _status = new();
    private readonly UILabel _diagnosticLabel = new();
    private readonly UILabel _capabilityLabel = new();
    private readonly UIButton _resetButton = new(UIButtonType.System);
    private readonly UIButton _debugButton = new(UIButtonType.System);
    private readonly UIButton _settingsButton = new(UIButtonType.System);
    private readonly HashSet<string> _planes = new();
    private readonly HashSet<string> _meshes = new();
    private SceneDelegate? _sceneDelegate;
    private NSTimer? _timer;
    private NSObject? _backgroundObserver;
    private NSObject? _activeObserver;
    private ARAnchor? _markerAnchor;
    private bool _visible;
    private volatile bool _running;
    private long _sessionGeneration;
    private bool _requestingPermission;
    private bool _showDebug;
    private string _trackingMessage = "正在准备摄像头…";
    private string _placementMessage = "缓慢移动手机，扫描有纹理且光线充足的水平面。";

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
        _sceneView.AddGestureRecognizer(new UITapGestureRecognizer(PlaceMarker));

        ConfigureLabel(_status, 16, 0);
        ConfigureLabel(_capabilityLabel, 12, 0);
        ConfigureLabel(_diagnosticLabel, 12, 0);
        _diagnosticLabel.Hidden = true;
        ConfigureButton(_resetButton, "重新扫描", ResetSession);
        ConfigureButton(_debugButton, "调试数据", ToggleDebug);
        ConfigureButton(_settingsButton, "打开相机设置", OpenSettings);
        _settingsButton.Hidden = true;

        _capabilityLabel.Text = "Alice Vision 0.0.2 · LiDAR + ARKit";
        if (ARWorldTrackingConfiguration.IsSupported)
        {
            bool lidar = ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.Mesh);
            _capabilityLabel.Text += lidar ? " · 真实深度 / 网格" : " · 此设备无 LiDAR 能力";
        }
        _backgroundObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.WillResignActiveNotification, _ => PauseSession());
        _activeObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIApplication.DidBecomeActiveNotification, _ =>
            {
                if (_visible)
                {
                    EnsureCameraAndStart();
                }
            });
        RefreshStatus();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        var bounds = View!.Bounds;
        var safe = View.SafeAreaInsets;
        _sceneView.Frame = bounds;
        nfloat x = safe.Left + 14;
        nfloat width = (nfloat)Math.Max(100, (double)(bounds.Width - safe.Left - safe.Right - 28));
        _status.Frame = new CGRect(x, safe.Top + 10, width, 94);
        _capabilityLabel.Frame = new CGRect(x, safe.Top + 108, width, 38);
        _diagnosticLabel.Frame = new CGRect(x, safe.Top + 154, width, 76);
        nfloat bottom = bounds.Height - safe.Bottom - 58;
        nfloat buttonWidth = (width - 10) / 2;
        _resetButton.Frame = new CGRect(x, bottom, buttonWidth, 44);
        _debugButton.Frame = new CGRect(x + buttonWidth + 10, bottom, buttonWidth, 44);
        _settingsButton.Frame = new CGRect(x, bottom - 54, width, 44);
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
            ? UIInterfaceOrientationMask.All
            : UIInterfaceOrientationMask.AllButUpsideDown;

    private void ConfigureLabel(UILabel label, nfloat size, nint lines)
    {
        label.Font = UIFont.SystemFontOfSize(size)!;
        label.TextColor = UIColor.White;
        label.BackgroundColor = UIColor.Black.ColorWithAlpha(0.65f);
        label.TextAlignment = UITextAlignment.Center;
        label.Lines = lines;
        label.Layer.CornerRadius = 10;
        label.ClipsToBounds = true;
        label.UserInteractionEnabled = false;
        View!.AddSubview(label);
    }

    private void ConfigureButton(UIButton button, string title, Action action)
    {
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(UIColor.White, UIControlState.Normal);
        button.BackgroundColor = UIColor.FromRGB(35, 52, 74).ColorWithAlpha(0.95f);
        button.Layer.CornerRadius = 12;
        button.TouchUpInside += (_, _) => action();
        View!.AddSubview(button);
    }

    private async void EnsureCameraAndStart()
    {
        if (!_visible || _running || _requestingPermission ||
            UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
        {
            return;
        }

        if (!ARWorldTrackingConfiguration.IsSupported ||
            !ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.Mesh))
        {
            _trackingMessage = "此版本需要 LiDAR 设备。";
            _placementMessage = "请使用带 LiDAR 的 iPhone Pro 或 iPad Pro；模拟器不能测试空间扫描。";
            _resetButton.Enabled = false;
            _debugButton.Enabled = false;
            RefreshStatus();
            return;
        }

        var permission = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (permission == AVAuthorizationStatus.NotDetermined)
        {
            _requestingPermission = true;
            try
            {
                await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video);
            }
            finally
            {
                _requestingPermission = false;
            }
            permission = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        }

        if (permission != AVAuthorizationStatus.Authorized)
        {
            _trackingMessage = "未获得相机权限。";
            _placementMessage = "请在系统设置中允许相机，再返回应用。";
            _settingsButton.Hidden = false;
            RefreshStatus();
            return;
        }

        _settingsButton.Hidden = true;
        if (_visible && UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active)
        {
            RunSession(false);
        }
    }

    private void RunSession(bool reset)
    {
        Interlocked.Increment(ref _sessionGeneration);
        using var configuration = new ARWorldTrackingConfiguration
        {
            PlaneDetection = ARPlaneDetection.Horizontal,
            LightEstimationEnabled = true,
            WorldAlignment = ARWorldAlignment.Gravity,
            SceneReconstruction = ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.MeshWithClassification)
                ? ARSceneReconstruction.MeshWithClassification
                : ARSceneReconstruction.Mesh
        };
        if (ARWorldTrackingConfiguration.SupportsFrameSemantics(ARFrameSemantics.SmoothedSceneDepth))
        {
            configuration.FrameSemantics |= ARFrameSemantics.SmoothedSceneDepth;
        }
        if (ARWorldTrackingConfiguration.SupportsFrameSemantics(ARFrameSemantics.SceneDepth))
        {
            configuration.FrameSemantics |= ARFrameSemantics.SceneDepth;
        }
        if (reset)
        {
            _planes.Clear();
            _meshes.Clear();
            _markerAnchor?.Dispose();
            _markerAnchor = null;
            _placementMessage = "扫描水平面，蓝色区域出现后点击放置标记。";
        }

        _trackingMessage = reset ? "正在重新建立空间追踪…" : "正在启动 / 恢复空间追踪…";
        _sceneView.Session.Run(configuration, reset
            ? ARSessionRunOptions.ResetTracking | ARSessionRunOptions.RemoveExistingAnchors
            : ARSessionRunOptions.None);
        _running = true;
        _sceneView.Playing = true;
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = NSTimer.CreateRepeatingScheduledTimer(TimeSpan.FromSeconds(0.5), _ => UpdateFrameDiagnostics());
        RefreshStatus();
    }

    private void PauseSession()
    {
        _running = false;
        Interlocked.Increment(ref _sessionGeneration);
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
        _sceneView.Session.Pause();
        _sceneView.Playing = false;
        _trackingMessage = "追踪已暂停，返回应用后继续。";
        RefreshStatus();
    }

    private void ResetSession()
    {
        if (_visible && ARWorldTrackingConfiguration.IsSupported &&
            ARWorldTrackingConfiguration.SupportsSceneReconstruction(ARSceneReconstruction.Mesh) &&
            AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video) == AVAuthorizationStatus.Authorized)
        {
            RunSession(true);
        }
        else
        {
            EnsureCameraAndStart();
        }
    }

    private void ToggleDebug()
    {
        _showDebug = !_showDebug;
        _diagnosticLabel.Hidden = !_showDebug;
        _sceneView.DebugOptions = _showDebug ? ARSCNDebugOptions.ShowFeaturePoints : SCNDebugOptions.None;
        _debugButton.SetTitle(_showDebug ? "隐藏调试" : "调试数据", UIControlState.Normal);
        if (_showDebug)
        {
            UpdateFrameDiagnostics();
        }
    }

    private void OpenSettings()
    {
        using var url = new NSUrl(UIApplication.OpenSettingsUrlString);
        UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
    }

    private void PlaceMarker(UITapGestureRecognizer gesture)
    {
        if (!_running || gesture.State != UIGestureRecognizerState.Ended)
        {
            return;
        }
        using var frame = _sceneView.Session.CurrentFrame;
        if (frame is null || frame.Camera.TrackingState != ARTrackingState.Normal)
        {
            _placementMessage = "等待追踪稳定后再点击。";
            RefreshStatus();
            return;
        }

        // UIView coordinates match this ARSCNView. Its raycast API applies camera
        // display transform, interface rotation and aspect-fill crop internally.
        using var query = _sceneView.CreateRaycastQuery(gesture.LocationInView(_sceneView),
            ARRaycastTarget.ExistingPlaneGeometry, ARRaycastTargetAlignment.Horizontal);
        if (query is null)
        {
            return;
        }
        var hits = _sceneView.Session.Raycast(query);
        try
        {
            if (hits.Length == 0)
            {
                _placementMessage = "这里还没有检测到水平面，请点击蓝色区域。";
                RefreshStatus();
                return;
            }
            if (_markerAnchor is not null)
            {
                _sceneView.Session.RemoveAnchor(_markerAnchor);
                _markerAnchor.Dispose();
            }
            _markerAnchor = new ARAnchor(SceneDelegate.MarkerAnchorName, hits[0].WorldTransform);
            _sceneView.Session.AddAnchor(_markerAnchor);
            _placementMessage = "标记已固定在真实水平面。再次点击可移动；切后台后需重新定位。";
            RefreshStatus();
        }
        finally
        {
            foreach (var hit in hits)
            {
                hit.Dispose();
            }
        }
    }

    private void UpdateFrameDiagnostics()
    {
        if (!_running || !_showDebug)
        {
            return;
        }
        using var frame = _sceneView.Session.CurrentFrame;
        if (frame is null)
        {
            _diagnosticLabel.Text = "正在等待相机帧…";
            return;
        }
        try
        {
            _diagnosticLabel.Text = LidarDiagnostics.Analyze(frame, _meshes.Count);
        }
        catch (Exception ex)
        {
            _diagnosticLabel.Text = $"诊断暂停：{ex.GetType().Name}";
        }
    }

    internal void UpdateTracking(ARCamera camera)
    {
        var state = camera.TrackingState;
        var reason = camera.TrackingStateReason;
        long generation = Interlocked.Read(ref _sessionGeneration);
        BeginInvokeOnMainThread(() =>
        {
            if (!_running || generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }
            _trackingMessage = state switch
            {
                ARTrackingState.Normal => $"追踪稳定 · 已找到 {_planes.Count} 个水平面",
                ARTrackingState.NotAvailable => "空间追踪暂不可用。",
                _ => reason switch
                {
                    ARTrackingStateReason.ExcessiveMotion => "移动过快，请放慢手机。",
                    ARTrackingStateReason.InsufficientFeatures => "纹理不足：对准有纹理的地板或桌面，增加光线。",
                    ARTrackingStateReason.Relocalizing => "正在重新定位，请对准刚才扫描的区域；失败时点击重新扫描。",
                    _ => "追踪初始化中，请缓慢移动手机。"
                }
            };
            RefreshStatus();
        });
    }

    internal void UpdatePlane(ARAnchor anchor, bool remove)
    {
        string identifier = anchor.Identifier.AsString();
        long generation = Interlocked.Read(ref _sessionGeneration);
        BeginInvokeOnMainThread(() =>
        {
            if (!_running || generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }
            if (remove)
            {
                _planes.Remove(identifier);
            }
            else
            {
                _planes.Add(identifier);
            }
            if (_planes.Count > 0 && _markerAnchor is null)
            {
                _placementMessage = "已检测到水平面（可能是地板或桌面）。点击蓝色区域放置标记。";
            }
            using var current = _sceneView.Session.CurrentFrame;
            if (current?.Camera.TrackingState == ARTrackingState.Normal)
            {
                _trackingMessage = $"追踪稳定 · 已找到 {_planes.Count} 个水平面";
            }
            RefreshStatus();
        });
    }

    internal void UpdateMesh(ARAnchor anchor, bool remove)
    {
        string identifier = anchor.Identifier.AsString();
        long generation = Interlocked.Read(ref _sessionGeneration);
        BeginInvokeOnMainThread(() =>
        {
            if (!_running || generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }
            if (remove)
            {
                _meshes.Remove(identifier);
            }
            else
            {
                _meshes.Add(identifier);
            }
        });
    }

    internal void Interrupted(bool ended)
    {
        long generation = Interlocked.Read(ref _sessionGeneration);
        BeginInvokeOnMainThread(() =>
        {
            if (!_running || generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }
            _trackingMessage = ended ? "中断结束，正在重新定位…" : "相机 / 空间追踪被系统中断。";
            RefreshStatus();
            if (ended && _visible && _running)
            {
                RunSession(false);
            }
        });
    }

    internal void SessionFailed(NSError error)
    {
        string description = error.LocalizedDescription;
        long generation = Interlocked.Read(ref _sessionGeneration);
        BeginInvokeOnMainThread(() =>
        {
            if (!_running || generation != Interlocked.Read(ref _sessionGeneration))
            {
                return;
            }
            PauseSession();
            _trackingMessage = "空间追踪启动失败。";
            _placementMessage = description + " 可点击重新扫描重试。";
            RefreshStatus();
        });
    }

    private void RefreshStatus() => _status.Text = _trackingMessage + "\n" + _placementMessage;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _visible = false;
            _running = false;
            Interlocked.Increment(ref _sessionGeneration);
            _timer?.Invalidate();
            _timer?.Dispose();
            _backgroundObserver?.Dispose();
            _activeObserver?.Dispose();
            _sceneView.Session.Pause();
            _sceneView.Delegate = null;
            _sceneDelegate?.Dispose();
            _markerAnchor?.Dispose();
        }
        base.Dispose(disposing);
    }
}
