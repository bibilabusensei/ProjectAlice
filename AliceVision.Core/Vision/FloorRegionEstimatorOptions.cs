namespace AliceVision.Core.Vision;

public sealed record FloorRegionEstimatorOptions
{
    public double DefaultHorizonY { get; init; } = 0.58;

    public double MinSearchY { get; init; } = 0.28;

    public double MaxSearchY { get; init; } = 0.82;

    public int TargetSamplesAcross { get; init; } = 96;

    public double ReliableConfidence { get; init; } = 0.35;
}
