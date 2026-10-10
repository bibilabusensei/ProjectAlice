using System.Buffers;
using System.Diagnostics;
using AliceVision.Core.Vision;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using AndroidActivity = Android.App.Activity;
using CameraSize = Android.Util.Size;

namespace AliceVision.Android.Camera;

/// <summary>
/// Owns the Android Camera2 lifecycle and forwards low-rate luminance frames to
/// the platform-neutral Alice Vision estimator.
/// </summary>
internal sealed class CameraController
{
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromMilliseconds(180);

    private readonly AndroidActivity _activity;
    private readonly TextureView _preview;
    private readonly Action<FloorEstimate> _onEstimate;
    private readonly Action<string> _onStatus;
    private readonly FloorRegionEstimator _estimator = new();
    private readonly FloorEstimateSmoother _smoother = new();
    private readonly CameraDeviceCallback _deviceCallback;
    private readonly FrameListener _frameListener;

    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private ImageReader? _imageReader;
    private Surface? _previewSurface;
    private CameraSize? _previewSize;
    private CameraSize? _analysisSize;
    private string? _cameraId;
    private int _frameRotationDegrees;
    private long _lastAnalysisTimestamp;
    private int _analysisBusy;
    private bool _opening;

    public CameraController(
        AndroidActivity activity,
        TextureView preview,
        Action<FloorEstimate> onEstimate,
        Action<string> onStatus)
    {
        _activity = activity;
        _preview = preview;
        _onEstimate = onEstimate;
        _onStatus = onStatus;
        _deviceCallback = new CameraDeviceCallback(this);
        _frameListener = new FrameListener(this);
    }

    public void Start()
    {
        if (_cameraThread is not null)
        {
            return;
        }

        _cameraThread = new HandlerThread("AliceVision.Camera");
        _cameraThread.Start();
        _cameraHandler = new Handler(_cameraThread.Looper!);
    }

    public void Stop()
    {
        CloseCamera();

        HandlerThread? thread = _cameraThread;
        _cameraThread = null;
        _cameraHandler = null;

        if (thread is not null)
        {
            thread.QuitSafely();
            thread.Join();
            thread.Dispose();
        }
    }

    public void OpenCamera()
    {
        if (_opening || _cameraDevice is not null || _cameraHandler is null || !_preview.IsAvailable)
        {
            return;
        }

        try
        {
            CameraManager manager = (CameraManager?)_activity.GetSystemService(Context.CameraService)
                ?? throw new InvalidOperationException("Android camera service is unavailable.");

            SelectBackCamera(manager);
            _opening = true;
            ReportStatus("正在打开后置摄像头…");
            manager.OpenCamera(_cameraId!, _deviceCallback, _cameraHandler);
        }
        catch (Exception exception)
        {
            _opening = false;
            ReportStatus($"相机启动失败：{exception.Message}");
        }
    }

    public void CloseCamera()
    {
        _opening = false;
        _smoother.Reset();

        _captureSession?.Close();
        _captureSession?.Dispose();
        _captureSession = null;

        _cameraDevice?.Close();
        _cameraDevice?.Dispose();
        _cameraDevice = null;

        _imageReader?.Close();
        _imageReader?.Dispose();
        _imageReader = null;

        _previewSurface?.Dispose();
        _previewSurface = null;
    }

    public void ConfigurePreviewTransform(int viewWidth, int viewHeight)
    {
        if (_previewSize is null || viewWidth <= 0 || viewHeight <= 0)
        {
            return;
        }

        SurfaceOrientation rotation =
            _activity.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
        var matrix = new Matrix();
        var viewRect = new RectF(0, 0, viewWidth, viewHeight);
        var bufferRect = new RectF(0, 0, _previewSize.Height, _previewSize.Width);
        float centerX = viewRect.CenterX();
        float centerY = viewRect.CenterY();

        if (rotation is SurfaceOrientation.Rotation90 or SurfaceOrientation.Rotation270)
        {
            bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
            matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
            float scale = Math.Max(
                viewHeight / (float)_previewSize.Height,
                viewWidth / (float)_previewSize.Width);
            matrix.PostScale(scale, scale, centerX, centerY);
            matrix.PostRotate(90 * ((int)rotation - 2), centerX, centerY);
        }
        else if (rotation == SurfaceOrientation.Rotation180)
        {
            matrix.PostRotate(180, centerX, centerY);
        }

        _preview.SetTransform(matrix);
    }

    private void SelectBackCamera(CameraManager manager)
    {
        foreach (string id in manager.GetCameraIdList())
        {
            CameraCharacteristics characteristics = manager.GetCameraCharacteristics(id);
            int facing = (characteristics.Get(CameraCharacteristics.LensFacing) as Java.Lang.Integer)?.IntValue()
                ?? (int)LensFacing.External;

            if (facing == (int)LensFacing.Front)
            {
                continue;
            }

            var map = characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                as StreamConfigurationMap;

            if (map is null)
            {
                continue;
            }

            CameraSize[] previewSizes = map.GetOutputSizes(
                Java.Lang.Class.FromType(typeof(SurfaceTexture))) ?? Array.Empty<CameraSize>();
            CameraSize[] analysisSizes =
                map.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? Array.Empty<CameraSize>();

            _cameraId = id;
            _previewSize = ChoosePreviewSize(previewSizes);
            _analysisSize = ChooseAnalysisSize(analysisSizes);

            int sensorOrientation =
                (characteristics.Get(CameraCharacteristics.SensorOrientation) as Java.Lang.Integer)?.IntValue()
                ?? 90;
            _frameRotationDegrees = CalculateFrameRotation(sensorOrientation);
            return;
        }

        throw new InvalidOperationException("No usable rear camera was found.");
    }

    private void OnCameraOpened(CameraDevice camera)
    {
        _opening = false;
        _cameraDevice = camera;

        if (_preview.SurfaceTexture is not { } texture ||
            _previewSize is null ||
            _analysisSize is null ||
            _cameraHandler is null)
        {
            CloseCamera();
            ReportStatus("相机预览表面尚未准备好。");
            return;
        }

        texture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);
        var previewSurface = new Surface(texture);
        _previewSurface = previewSurface;
        _imageReader = ImageReader.NewInstance(
            _analysisSize.Width,
            _analysisSize.Height,
            ImageFormatType.Yuv420888,
            maxImages: 2);
        _imageReader.SetOnImageAvailableListener(_frameListener, _cameraHandler);
        Surface analysisSurface = _imageReader.Surface
            ?? throw new InvalidOperationException("Camera analysis surface was not created.");

        CaptureRequest.Builder requestBuilder = camera.CreateCaptureRequest(CameraTemplate.Preview);
        requestBuilder.AddTarget(previewSurface);
        requestBuilder.AddTarget(analysisSurface);

        var outputs = new List<Surface>
        {
            previewSurface,
            analysisSurface
        };

#pragma warning disable CA1422 // Legacy overload keeps support for Android 7-10.
        camera.CreateCaptureSession(
            outputs,
            new CaptureSessionCallback(this, requestBuilder),
            _cameraHandler);
#pragma warning restore CA1422

        _preview.Post(() => ConfigurePreviewTransform(_preview.Width, _preview.Height));
    }

    private void OnSessionConfigured(
        CameraCaptureSession session,
        CaptureRequest.Builder requestBuilder)
    {
        if (_cameraDevice is null || _cameraHandler is null)
        {
            session.Close();
            return;
        }

        try
        {
            _captureSession = session;
            session.SetRepeatingRequest(requestBuilder.Build(), null, _cameraHandler);
            ReportStatus("相机已启动，正在搜索地面…");
        }
        catch (Exception exception)
        {
            ReportStatus($"相机预览启动失败：{exception.Message}");
        }
    }

    private void AnalyzeLatestFrame(ImageReader reader)
    {
        using Image? image = reader.AcquireLatestImage();

        if (image is null)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (now - _lastAnalysisTimestamp) / (double)Stopwatch.Frequency;

        if (_lastAnalysisTimestamp != 0 && elapsedSeconds < AnalysisInterval.TotalSeconds)
        {
            return;
        }

        if (Interlocked.Exchange(ref _analysisBusy, 1) != 0)
        {
            return;
        }

        _lastAnalysisTimestamp = now;
        byte[]? source = null;
        byte[]? upright = null;

        try
        {
            Image.Plane[]? planes = image.GetPlanes();

            if (planes is not { Length: > 0 } || planes[0] is not { } yPlane)
            {
                throw new InvalidOperationException("Camera image has no luminance plane.");
            }

            Java.Nio.ByteBuffer yBuffer = yPlane.Buffer
                ?? throw new InvalidOperationException("Camera luminance buffer is unavailable.");
            int sourceLength = yBuffer.Remaining();
            source = ArrayPool<byte>.Shared.Rent(sourceLength);
            upright = ArrayPool<byte>.Shared.Rent(checked(image.Width * image.Height));
            yBuffer.Get(source, 0, sourceLength);

            ImageInfo info = LumaPlaneTransformer.CopyToPackedUpright(
                source.AsSpan(0, sourceLength),
                image.Width,
                image.Height,
                yPlane.RowStride,
                yPlane.PixelStride,
                _frameRotationDegrees,
                upright);
            FloorEstimate estimate = _estimator.Estimate(
                upright.AsSpan(0, checked(info.Width * info.Height)),
                info.Width,
                info.Height);
            FloorEstimate smoothed = _smoother.Update(estimate);

            _activity.RunOnUiThread(() => _onEstimate(smoothed));
        }
        catch (Exception exception)
        {
            ReportStatus($"画面分析失败：{exception.Message}");
        }
        finally
        {
            if (source is not null)
            {
                ArrayPool<byte>.Shared.Return(source);
            }

            if (upright is not null)
            {
                ArrayPool<byte>.Shared.Return(upright);
            }

            Interlocked.Exchange(ref _analysisBusy, 0);
        }
    }

    private int CalculateFrameRotation(int sensorOrientation)
    {
        SurfaceOrientation displayRotation =
            _activity.WindowManager?.DefaultDisplay?.Rotation ?? SurfaceOrientation.Rotation0;
        int displayDegrees = displayRotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0
        };

        return (sensorOrientation - displayDegrees + 360) % 360;
    }

    private static CameraSize ChoosePreviewSize(IEnumerable<CameraSize> sizes)
    {
        CameraSize? preferred = sizes
            .Where(size => size.Width <= 1920 && size.Height <= 1080)
            .OrderBy(size => Math.Abs(size.Width / (double)size.Height - 16.0 / 9.0))
            .ThenBy(size => Math.Abs(size.Width - 1280))
            .FirstOrDefault();

        return preferred ?? sizes.FirstOrDefault() ?? new CameraSize(1280, 720);
    }

    private static CameraSize ChooseAnalysisSize(IEnumerable<CameraSize> sizes)
    {
        CameraSize? preferred = sizes
            .Where(size => size.Width * size.Height <= 640 * 480)
            .OrderByDescending(size => size.Width * size.Height)
            .FirstOrDefault();

        return preferred
            ?? sizes.OrderBy(size => size.Width * size.Height).FirstOrDefault()
            ?? new CameraSize(640, 480);
    }

    private void ReportStatus(string message)
    {
        _activity.RunOnUiThread(() => _onStatus(message));
    }

    private sealed class CameraDeviceCallback : CameraDevice.StateCallback
    {
        private readonly CameraController _owner;

        public CameraDeviceCallback(CameraController owner)
        {
            _owner = owner;
        }

        public override void OnOpened(CameraDevice camera) => _owner.OnCameraOpened(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            _owner._cameraDevice = null;
            _owner._opening = false;
            _owner.ReportStatus("相机连接已断开。");
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            _owner._cameraDevice = null;
            _owner._opening = false;
            _owner.ReportStatus($"相机错误：{error}");
        }
    }

    private sealed class CaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly CameraController _owner;
        private readonly CaptureRequest.Builder _requestBuilder;

        public CaptureSessionCallback(
            CameraController owner,
            CaptureRequest.Builder requestBuilder)
        {
            _owner = owner;
            _requestBuilder = requestBuilder;
        }

        public override void OnConfigured(CameraCaptureSession session) =>
            _owner.OnSessionConfigured(session, _requestBuilder);

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            session.Close();
            _owner.ReportStatus("设备不支持当前相机输出组合。");
        }
    }

    private sealed class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly CameraController _owner;

        public FrameListener(CameraController owner)
        {
            _owner = owner;
        }

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null)
            {
                _owner.AnalyzeLatestFrame(reader);
            }
        }
    }
}
