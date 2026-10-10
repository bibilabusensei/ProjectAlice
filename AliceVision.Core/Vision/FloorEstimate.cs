namespace AliceVision.Core.Vision;

/// <summary>
/// A lightweight candidate boundary for an experimental lower-image overlay.
/// Coordinates are normalized to the upright image, from 0 at the top to 1 at the bottom.
/// IsReliable expresses edge strength, not verified floor or walkability semantics.
/// </summary>
public readonly record struct FloorEstimate(
    double HorizonY,
    double Confidence,
    bool IsReliable,
    double AverageLuminance,
    double EdgeStrength)
{
    public double FloorCoverage => 1.0 - HorizonY;
}
