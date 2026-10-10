using AliceVision.Core.Vision;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AliceVision.Android.Views;

/// <summary>
/// Draws the current coarse floor estimate over the live camera preview.
/// </summary>
public sealed class FloorOverlayView : View
{
    private readonly Paint _floorPaint = new(PaintFlags.AntiAlias);

    private readonly Paint _linePaint = new(PaintFlags.AntiAlias)
    {
        StrokeWidth = 5
    };

    private readonly Paint _labelPaint = new(PaintFlags.AntiAlias)
    {
        TextSize = 36
    };

    private FloorEstimate _estimate = new(0.58, 0, false, 0, 0);

    public FloorOverlayView(Context context)
        : base(context)
    {
        _floorPaint.SetStyle(Paint.Style.Fill);
        _linePaint.SetStyle(Paint.Style.Stroke);
        _labelPaint.SetTypeface(Typeface.DefaultBold);
        SetWillNotDraw(false);
    }

    public void UpdateEstimate(FloorEstimate estimate)
    {
        _estimate = estimate;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        float horizonY = (float)(_estimate.HorizonY * Height);
        int fillAlpha = 28 + (int)Math.Round(_estimate.Confidence * 62);
        Color accent = _estimate.IsReliable
            ? Color.Rgb(55, 230, 165)
            : Color.Rgb(255, 184, 77);

        _floorPaint.Color = Color.Argb(fillAlpha, accent.R, accent.G, accent.B);
        _linePaint.Color = accent;
        _labelPaint.Color = Color.White;

        canvas.DrawRect(0, horizonY, Width, Height, _floorPaint);
        canvas.DrawLine(0, horizonY, Width, horizonY, _linePaint);
        canvas.DrawText(
            _estimate.IsReliable ? "FLOOR CANDIDATE" : "SEARCHING",
            24,
            Math.Max(42, horizonY - 16),
            _labelPaint);
    }
}
