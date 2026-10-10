using AliceVision.Android.Camera;
using AliceVision.Android.Views;
using AliceVision.Core.Vision;
using Android.Content.PM;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace AliceVision.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    ScreenOrientation = ScreenOrientation.Portrait,
    Theme = "@android:style/Theme.Material.NoActionBar")]
public sealed class MainActivity : Activity, TextureView.ISurfaceTextureListener
{
    private const int CameraPermissionRequestCode = 1001;

    private TextureView? _preview;
    private TextView? _statusText;
    private FloorOverlayView? _overlay;
    private CameraController? _cameraController;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        var root = FindViewById<FrameLayout>(Resource.Id.root)
            ?? throw new InvalidOperationException("Root camera layout was not found.");
        _preview = FindViewById<TextureView>(Resource.Id.camera_preview)
            ?? throw new InvalidOperationException("Camera preview was not found.");
        _statusText = FindViewById<TextView>(Resource.Id.status_text)
            ?? throw new InvalidOperationException("Status text was not found.");

        _overlay = new FloorOverlayView(this);
        root.AddView(
            _overlay,
            new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent));
        _statusText.BringToFront();

        _preview.SurfaceTextureListener = this;
        _cameraController = new CameraController(
            this,
            _preview,
            OnFloorEstimate,
            SetStatus);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _cameraController?.Start();
        EnsurePermissionAndOpenCamera();
    }

    protected override void OnPause()
    {
        _cameraController?.Stop();
        base.OnPause();
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != CameraPermissionRequestCode)
        {
            return;
        }

        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            SetStatus("相机已授权，正在启动…");
            _cameraController?.OpenCamera();
        }
        else
        {
            SetStatus("需要相机权限才能分析地面。请在系统设置中允许相机权限。");
        }
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        EnsurePermissionAndOpenCamera();
    }

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        _cameraController?.CloseCamera();
        return true;
    }

    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height)
    {
        _cameraController?.ConfigurePreviewTransform(width, height);
    }

    public void OnSurfaceTextureUpdated(SurfaceTexture surface)
    {
    }

    private void EnsurePermissionAndOpenCamera()
    {
        if (_preview?.IsAvailable != true)
        {
            SetStatus("正在准备相机预览…");
            return;
        }

        if (CheckSelfPermission(global::Android.Manifest.Permission.Camera) == Permission.Granted)
        {
            _cameraController?.OpenCamera();
            return;
        }

        SetStatus("正在等待相机权限…");
        RequestPermissions(
            new[] { global::Android.Manifest.Permission.Camera },
            CameraPermissionRequestCode);
    }

    private void OnFloorEstimate(FloorEstimate estimate)
    {
        _overlay?.UpdateEstimate(estimate);

        string reliability = estimate.IsReliable ? "已锁定候选地面" : "正在搜索地面";
        SetStatus(
            $"{reliability}  ·  可信度 {estimate.Confidence:P0}  ·  " +
            $"亮度 {estimate.AverageLuminance:F0}");
    }

    private void SetStatus(string message)
    {
        if (_statusText is not null)
        {
            _statusText.Text = message;
        }
    }
}
