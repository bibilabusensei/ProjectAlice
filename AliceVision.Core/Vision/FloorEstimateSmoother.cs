namespace AliceVision.Core.Vision;

/// <summary>
/// Reduces frame-to-frame jitter before an estimate is drawn over a live preview.
/// </summary>
public sealed class FloorEstimateSmoother
{
    private readonly double _trackingAlpha;
    private FloorEstimate? _previous;

    public FloorEstimateSmoother(double trackingAlpha = 0.22)
    {
        if (!double.IsFinite(trackingAlpha) || trackingAlpha is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(trackingAlpha));
        }

        _trackingAlpha = trackingAlpha;
    }

    public FloorEstimate Update(FloorEstimate current)
    {
        if (!double.IsFinite(current.HorizonY) || current.HorizonY is < 0 or > 1 ||
            !double.IsFinite(current.Confidence) || current.Confidence is < 0 or > 1 ||
            !double.IsFinite(current.AverageLuminance) || current.AverageLuminance is < 0 or > 255 ||
            !double.IsFinite(current.EdgeStrength) || current.EdgeStrength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current), "Estimate values must be finite and within their valid ranges.");
        }

        if (_previous is not { } previous)
        {
            _previous = current;
            return current;
        }

        double alpha = current.IsReliable ? _trackingAlpha : _trackingAlpha * 0.35;
        var smoothed = new FloorEstimate(
            Lerp(previous.HorizonY, current.HorizonY, alpha),
            Lerp(previous.Confidence, current.Confidence, 0.30),
            current.IsReliable,
            Lerp(previous.AverageLuminance, current.AverageLuminance, 0.20),
            Lerp(previous.EdgeStrength, current.EdgeStrength, 0.30));

        _previous = smoothed;
        return smoothed;
    }

    public void Reset() => _previous = null;

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;
}
