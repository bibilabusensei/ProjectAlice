// See https://aka.ms/new-console-template for more information
using AliceVision.Core.Vision;

var tests = new (string Name, Action Body)[]
{
    ("finds a strong synthetic image boundary", FindsStrongBoundary),
    ("finds a reversed-polarity image boundary", FindsReversedBoundary),
    ("falls back safely on a flat image", FallsBackOnFlatImage),
    ("supports Android-style row stride", SupportsRowStride),
    ("supports pixel stride and ignores padding", SupportsPixelStride),
    ("keeps a luminance plane at 0 degrees", () => RotatesLumaPlane(0)),
    ("rotates a luminance plane 90 degrees clockwise", () => RotatesLumaPlane(90)),
    ("rotates a luminance plane 180 degrees clockwise", () => RotatesLumaPlane(180)),
    ("rotates a luminance plane 270 degrees clockwise", () => RotatesLumaPlane(270)),
    ("rotates padded camera planes with no final-row padding", RotatesPaddedPlanes),
    ("rotation and estimation agree for every camera orientation", RotationPreservesBoundary),
    ("rejects invalid rotations", RejectsInvalidRotations),
    ("rejects invalid plane dimensions and strides", RejectsInvalidPlanes),
    ("rejects an undersized destination", RejectsUndersizedDestination),
    ("rejects overlapping source and destination", RejectsOverlappingBuffers),
    ("rejects an undersized frame", RejectsUndersizedFrame),
    ("rejects invalid estimator dimensions and strides", RejectsInvalidEstimatorFrames),
    ("rejects non-finite and invalid estimator options", RejectsInvalidOptions),
    ("smooths successive estimates", SmoothsSuccessiveEstimates),
    ("slows unreliable estimates and resets history", SmootherSlowsAndResets),
    ("rejects invalid smoothing factors", RejectsInvalidSmoothingFactors),
    ("rejects malformed estimates without poisoning history", RejectsInvalidEstimates)
};

int failed = 0;

foreach ((string name, Action body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"[PASS] {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"[FAIL] {name}: {exception.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void FindsStrongBoundary()
{
    const int width = 160;
    const int height = 120;
    const int boundaryY = 72;
    byte[] frame = CreateTwoToneFrame(width, height, width, boundaryY, 52, 174);

    FloorEstimate estimate = new FloorRegionEstimator().Estimate(frame, width, height);

    Assert(estimate.IsReliable, "Expected the high-contrast boundary to be reliable.");
    AssertBetween(estimate.HorizonY, 0.56, 0.64, "Unexpected horizon position.");
    Assert(estimate.Confidence > 0.70, "Expected strong confidence.");
}

static void FallsBackOnFlatImage()
{
    const int width = 96;
    const int height = 80;
    byte[] frame = Enumerable.Repeat((byte)110, width * height).ToArray();

    FloorEstimate estimate = new FloorRegionEstimator().Estimate(frame, width, height);

    Assert(!estimate.IsReliable, "A flat image must not be reported as a reliable floor.");
    AssertBetween(estimate.HorizonY, 0.575, 0.585, "Fallback horizon changed unexpectedly.");
    AssertBetween(estimate.AverageLuminance, 109.9, 110.1, "Average luminance is incorrect.");
}

static void FindsReversedBoundary()
{
    byte[] frame = CreateTwoToneFrame(160, 120, 160, 72, 174, 52);
    FloorEstimate estimate = new FloorRegionEstimator().Estimate(frame, 160, 120);
    Assert(estimate.IsReliable, "Dark lower regions should detect the same edge.");
    AssertBetween(estimate.HorizonY, 0.56, 0.64, "Reversed contrast moved the boundary.");
}

static void SupportsRowStride()
{
    const int width = 64;
    const int height = 80;
    const int rowStride = 72;
    const int boundaryY = 44;
    byte[] frame = CreateTwoToneFrame(width, height, rowStride, boundaryY, 40, 190);

    FloorEstimate estimate = new FloorRegionEstimator().Estimate(
        frame,
        width,
        height,
        rowStride);

    Assert(estimate.IsReliable, "The strided frame boundary was not detected.");
    AssertBetween(estimate.HorizonY, 0.50, 0.62, "The strided frame horizon is misplaced.");
}

static void SupportsPixelStride()
{
    const int width = 64;
    const int height = 80;
    const int pixelStride = 2;
    const int rowStride = 137;
    byte[] packed = CreateTwoToneFrame(width, height, width, 44, 40, 190);
    byte[] strided = CreateStridedPlane(packed, width, height, rowStride, pixelStride);
    var estimator = new FloorRegionEstimator();
    FloorEstimate expected = estimator.Estimate(packed, width, height);
    FloorEstimate actual = estimator.Estimate(strided, width, height, rowStride, pixelStride);
    Assert(actual == expected, "Padding or unused interleaved bytes affected the estimate.");

    byte[] implicitStride = CreateStridedPlane(packed, width, height, (width - 1) * pixelStride + 1, pixelStride);
    Assert(estimator.Estimate(implicitStride, width, height, pixelStride: pixelStride) == expected,
        "Default row stride is wrong for interleaved samples.");
}

static void RotatesLumaPlane(int rotation)
{
    byte[] source =
    {
        1, 2, 3,
        4, 5, 6
    };
    byte[] destination = new byte[6];

    ImageInfo result = LumaPlaneTransformer.CopyToPackedUpright(
        source,
        width: 3,
        height: 2,
        rowStride: 3,
        pixelStride: 1,
        clockwiseRotationDegrees: rotation,
        destination);

    byte[] expected = rotation switch
    {
        0 => new byte[] { 1, 2, 3, 4, 5, 6 },
        90 => new byte[] { 4, 1, 5, 2, 6, 3 },
        180 => new byte[] { 6, 5, 4, 3, 2, 1 },
        270 => new byte[] { 3, 6, 2, 5, 1, 4 },
        _ => throw new InvalidOperationException()
    };

    ImageInfo expectedInfo = rotation is 90 or 270 ? new ImageInfo(2, 3) : new ImageInfo(3, 2);
    Assert(result == expectedInfo, "Rotated dimensions are incorrect.");
    Assert(destination.SequenceEqual(expected), "Rotated pixels are incorrect.");
}

static void RotatesPaddedPlanes()
{
    byte[] source = CreateStridedPlane(new byte[] { 1, 2, 3, 4, 5, 6 }, 3, 2, 11, 3);
    // Only real pixels of the final row are required; camera APIs need not expose trailing padding.
    Assert(source.Length == 18, "Synthetic final-row length is incorrect.");
    foreach (int rotation in new[] { 0, 90, 180, 270 })
    {
        byte[] expected = new byte[6];
        byte[] output = Enumerable.Repeat((byte)255, 9).ToArray();
        ImageInfo expectedInfo = LumaPlaneTransformer.CopyToPackedUpright(new byte[] { 1, 2, 3, 4, 5, 6 }, 3, 2, 3, 1, rotation, expected);
        ImageInfo actualInfo = LumaPlaneTransformer.CopyToPackedUpright(source, 3, 2, 11, 3, rotation, output);
        Assert(actualInfo == expectedInfo && output.AsSpan(0, 6).SequenceEqual(expected), "Strided rotation changed pixels.");
        Assert(output.Skip(6).All(value => value == 255), "Rotation wrote beyond its declared destination image.");
    }
}

static void RotationPreservesBoundary()
{
    const int width = 96;
    const int height = 80;
    byte[] upright = CreateTwoToneFrame(width, height, width, 48, 40, 190);
    var estimator = new FloorRegionEstimator();
    FloorEstimate expected = estimator.Estimate(upright, width, height);
    foreach (int rotation in new[] { 0, 90, 180, 270 })
    {
        int inverseRotation = (360 - rotation) % 360;
        byte[] sensor = new byte[upright.Length];
        ImageInfo sensorInfo = LumaPlaneTransformer.CopyToPackedUpright(upright, width, height, width, 1, inverseRotation, sensor);
        byte[] strided = CreateStridedPlane(sensor, sensorInfo.Width, sensorInfo.Height, sensorInfo.Width * 2 + 7, 2);
        byte[] restored = new byte[upright.Length];
        ImageInfo info = LumaPlaneTransformer.CopyToPackedUpright(strided, sensorInfo.Width, sensorInfo.Height, sensorInfo.Width * 2 + 7, 2, rotation, restored);
        Assert(info == new ImageInfo(width, height) && restored.SequenceEqual(upright), "Camera orientation did not restore the upright frame.");
        Assert(estimator.Estimate(restored, info.Width, info.Height) == expected, "Rotation changed the boundary estimate.");
    }
}

static void RejectsInvalidRotations()
{
    foreach (int rotation in new[] { -90, 1, 45, 360 })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => LumaPlaneTransformer.CopyToPackedUpright(new byte[6], 3, 2, 3, 1, rotation, new byte[6]));
    }
}

static void RejectsInvalidPlanes()
{
    foreach ((int width, int height, int row, int pixel) in new[]
    {
        (0, 2, 3, 1), (3, -1, 3, 1), (3, 2, 0, 1), (3, 2, -1, 1), (3, 2, 3, 0), (3, 2, 3, -1), (3, 2, 4, 2)
    })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => LumaPlaneTransformer.CopyToPackedUpright(new byte[64], width, height, row, pixel, 0, new byte[64]));
    }
    AssertThrows<ArgumentException>(() => LumaPlaneTransformer.CopyToPackedUpright(new byte[5], 3, 2, 3, 1, 0, new byte[6]));
    AssertThrows<OverflowException>(() => LumaPlaneTransformer.CopyToPackedUpright(new byte[6], int.MaxValue, 2, int.MaxValue, 2, 0, new byte[6]));
}

static void RejectsUndersizedDestination() =>
    AssertThrows<ArgumentException>(() => LumaPlaneTransformer.CopyToPackedUpright(new byte[6], 3, 2, 3, 1, 90, new byte[5]));

static void RejectsOverlappingBuffers()
{
    byte[] buffer = { 1, 2, 3, 4, 5, 6, 7 };
    AssertThrows<ArgumentException>(() => LumaPlaneTransformer.CopyToPackedUpright(buffer.AsSpan(0, 6), 3, 2, 3, 1, 90, buffer.AsSpan(1, 6)));
    Assert(buffer.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7 }), "Rejected rotation mutated its buffer.");
}

static void RejectsUndersizedFrame()
{
    bool threw = false;

    try
    {
        _ = new FloorRegionEstimator().Estimate(new byte[16], 16, 16);
    }
    catch (ArgumentException)
    {
        threw = true;
    }

    Assert(threw, "Expected invalid buffer dimensions to throw.");
}

static void SmoothsSuccessiveEstimates()
{
    var smoother = new FloorEstimateSmoother(0.20);
    _ = smoother.Update(new FloorEstimate(0.50, 0.8, true, 100, 30));
    FloorEstimate result = smoother.Update(new FloorEstimate(0.70, 0.8, true, 100, 30));

    AssertBetween(result.HorizonY, 0.53, 0.55, "Unexpected smoothing result.");
}

static void RejectsInvalidEstimatorFrames()
{
    var estimator = new FloorRegionEstimator();
    foreach ((int width, int height, int row, int pixel) in new[]
    {
        (7, 8, 0, 1), (8, 7, 0, 1), (8, 8, -1, 1), (8, 8, 7, 1), (8, 8, 8, 0), (8, 8, 8, -1), (8, 8, 14, 2)
    })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => estimator.Estimate(new byte[256], width, height, row, pixel));
    }
}

static void RejectsInvalidOptions()
{
    foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorRegionEstimator(new() { DefaultHorizonY = value }));
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorRegionEstimator(new() { MinSearchY = value }));
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorRegionEstimator(new() { MaxSearchY = value }));
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorRegionEstimator(new() { ReliableConfidence = value }));
    }
    foreach (FloorRegionEstimatorOptions options in new FloorRegionEstimatorOptions[]
    {
        new() { DefaultHorizonY = 0 }, new() { DefaultHorizonY = 1 }, new() { MinSearchY = -0.1 },
        new() { MaxSearchY = 1.1 }, new() { MinSearchY = 0.8, MaxSearchY = 0.2 },
        new() { TargetSamplesAcross = 7 }, new() { ReliableConfidence = 0 }, new() { ReliableConfidence = 1.1 }
    })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorRegionEstimator(options));
    }
}

static void SmootherSlowsAndResets()
{
    var smoother = new FloorEstimateSmoother(0.20);
    var first = new FloorEstimate(0.5, 0.8, true, 100, 30);
    Assert(smoother.Update(first) == first, "First estimate should be unchanged.");
    var unreliable = new FloorEstimate(0.7, 0.1, false, 200, 5);
    FloorEstimate result = smoother.Update(unreliable);
    AssertBetween(result.HorizonY, 0.5139, 0.5141, "Unreliable estimate moved too quickly.");
    Assert(!result.IsReliable, "Smoothing must not promote an unreliable boundary.");
    smoother.Reset();
    Assert(smoother.Update(unreliable) == unreliable, "Reset left old orientation/history behind.");
}

static void RejectsInvalidSmoothingFactors()
{
    foreach (double alpha in new[] { 0, -0.1, 1.1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => new FloorEstimateSmoother(alpha));
    }
}

static void RejectsInvalidEstimates()
{
    var smoother = new FloorEstimateSmoother(0.2);
    var initial = new FloorEstimate(0.5, 0.8, true, 100, 30);
    _ = smoother.Update(initial);
    foreach (FloorEstimate invalid in new[]
    {
        initial with { HorizonY = double.NaN }, initial with { HorizonY = 1.1 },
        initial with { Confidence = double.PositiveInfinity }, initial with { Confidence = -0.1 },
        initial with { AverageLuminance = double.NegativeInfinity }, initial with { AverageLuminance = 256 },
        initial with { EdgeStrength = double.NaN }, initial with { EdgeStrength = -1 }
    })
    {
        AssertThrows<ArgumentOutOfRangeException>(() => smoother.Update(invalid));
    }
    Assert(smoother.Update(initial) == initial, "Rejected data poisoned the next valid estimate.");
}

static byte[] CreateStridedPlane(byte[] packed, int width, int height, int rowStride, int pixelStride)
{
    int length = (height - 1) * rowStride + (width - 1) * pixelStride + 1;
    byte[] frame = Enumerable.Repeat((byte)255, length).ToArray();
    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            frame[y * rowStride + x * pixelStride] = packed[y * width + x];
        }
    }
    return frame;
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static byte[] CreateTwoToneFrame(
    int width,
    int height,
    int rowStride,
    int boundaryY,
    byte upperValue,
    byte lowerValue)
{
    byte[] frame = new byte[rowStride * height];

    for (int y = 0; y < height; y++)
    {
        byte value = y < boundaryY ? upperValue : lowerValue;

        for (int x = 0; x < width; x++)
        {
            frame[y * rowStride + x] = (byte)(value + x % 3);
        }
    }

    return frame;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertBetween(double value, double minimum, double maximum, string message)
{
    Assert(value >= minimum && value <= maximum, $"{message} Value: {value:F4}");
}
