namespace AliceVision.Core.Vision;

/// <summary>
/// Finds a candidate boundary from the luminance plane of a single camera frame.
/// This first-stage estimator looks for a strong, horizontally coherent transition in
/// the lower-middle part of the image. It is intentionally small enough to run before
/// a future neural depth model is available.
/// Edge strength alone does not identify a floor, depth, or a safe walkable surface.
/// </summary>
public sealed class FloorRegionEstimator
{
    private readonly FloorRegionEstimatorOptions _options;

    public FloorRegionEstimator(FloorRegionEstimatorOptions? options = null)
    {
        _options = options ?? new FloorRegionEstimatorOptions();
        ValidateOptions(_options);
    }

    public FloorEstimate Estimate(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        int rowStride = 0,
        int pixelStride = 1)
    {
        ValidateFrame(luminance, width, height, ref rowStride, pixelStride);

        int xStart = Math.Max(0, width / 10);
        int xEnd = Math.Min(width - 1, width - width / 10);
        int sampleStepX = Math.Max(1, (xEnd - xStart) / _options.TargetSamplesAcross);
        int minY = Math.Max(1, (int)Math.Round(height * _options.MinSearchY));
        int maxY = Math.Min(height - 2, (int)Math.Round(height * _options.MaxSearchY));
        int rowStep = Math.Max(1, height / 180);
        int comparisonOffset = Math.Max(1, height / 120);

        double bestScore = 0;
        double bestCoherence = 0;
        int bestY = (int)Math.Round(height * _options.DefaultHorizonY);
        double scoreSum = 0;
        int scoreCount = 0;

        for (int y = minY; y <= maxY; y += rowStep)
        {
            int topY = Math.Max(0, y - comparisonOffset);
            int bottomY = Math.Min(height - 1, y + comparisonOffset);
            int topRow = topY * rowStride;
            int bottomRow = bottomY * rowStride;
            long signedDifference = 0;
            long absoluteDifference = 0;
            int samples = 0;

            for (int x = xStart; x <= xEnd; x += sampleStepX)
            {
                int top = luminance[topRow + x * pixelStride];
                int bottom = luminance[bottomRow + x * pixelStride];
                int difference = bottom - top;

                signedDifference += difference;
                absoluteDifference += Math.Abs(difference);
                samples++;
            }

            if (samples == 0)
            {
                continue;
            }

            double meanAbsoluteDifference = absoluteDifference / (double)samples;
            double coherence = absoluteDifference == 0
                ? 0
                : Math.Abs(signedDifference) / (double)absoluteDifference;
            double score = meanAbsoluteDifference * (0.25 + 0.75 * coherence);

            scoreSum += score;
            scoreCount++;

            if (score > bestScore)
            {
                bestScore = score;
                bestCoherence = coherence;
                bestY = y;
            }
        }

        double meanScore = scoreCount == 0 ? 0 : scoreSum / scoreCount;
        double absoluteStrength = Clamp01((bestScore - 4.0) / 28.0);
        double relativeStrength = Clamp01((bestScore - meanScore) / (meanScore + 6.0));
        double confidence = Clamp01(
            absoluteStrength * 0.55 +
            relativeStrength * 0.30 +
            bestCoherence * 0.15);
        bool isReliable = confidence >= _options.ReliableConfidence;

        double candidateHorizon = bestY / (double)(height - 1);
        double support = Clamp01(confidence / _options.ReliableConfidence);
        double horizon = Lerp(_options.DefaultHorizonY, candidateHorizon, support);
        double averageLuminance = CalculateAverageLuminance(
            luminance,
            width,
            height,
            rowStride,
            pixelStride);

        return new FloorEstimate(
            Clamp01(horizon),
            confidence,
            isReliable,
            averageLuminance,
            bestScore);
    }

    private static double CalculateAverageLuminance(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        int rowStride,
        int pixelStride)
    {
        int stepX = Math.Max(1, width / 64);
        int stepY = Math.Max(1, height / 64);
        long total = 0;
        int count = 0;

        for (int y = 0; y < height; y += stepY)
        {
            int row = y * rowStride;

            for (int x = 0; x < width; x += stepX)
            {
                total += luminance[row + x * pixelStride];
                count++;
            }
        }

        return count == 0 ? 0 : total / (double)count;
    }

    private static void ValidateFrame(
        ReadOnlySpan<byte> luminance,
        int width,
        int height,
        ref int rowStride,
        int pixelStride)
    {
        if (width < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be at least 8 pixels.");
        }

        if (height < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be at least 8 pixels.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelStride);

        int minimumRowStride = checked((width - 1) * pixelStride + 1);
        rowStride = rowStride == 0 ? minimumRowStride : rowStride;

        if (rowStride < minimumRowStride)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride), "Row stride is too small for the frame width.");
        }

        int requiredLength = checked((height - 1) * rowStride + minimumRowStride);

        if (luminance.Length < requiredLength)
        {
            throw new ArgumentException("The luminance buffer is smaller than the declared frame.", nameof(luminance));
        }
    }

    private static void ValidateOptions(FloorRegionEstimatorOptions options)
    {
        if (!double.IsFinite(options.DefaultHorizonY) || options.DefaultHorizonY is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Default horizon must be between 0 and 1.");
        }

        if (!double.IsFinite(options.MinSearchY) || !double.IsFinite(options.MaxSearchY) ||
            options.MinSearchY < 0 || options.MaxSearchY > 1 || options.MinSearchY >= options.MaxSearchY)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The horizon search range is invalid.");
        }

        if (options.TargetSamplesAcross < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "At least 8 horizontal samples are required.");
        }

        if (!double.IsFinite(options.ReliableConfidence) || options.ReliableConfidence is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Reliable confidence must be between 0 and 1.");
        }
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static double Lerp(double start, double end, double amount) =>
        start + (end - start) * amount;
}
