namespace AliceVision.Core.Vision;

/// <summary>
/// Converts a strided camera luminance plane into a packed, upright grayscale frame.
/// </summary>
public static class LumaPlaneTransformer
{
    public static ImageInfo CopyToPackedUpright(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int rowStride,
        int pixelStride,
        int clockwiseRotationDegrees,
        Span<byte> destination)
    {
        ValidateRotation(clockwiseRotationDegrees);
        ValidateSource(source, width, height, rowStride, pixelStride);

        bool swapsDimensions = clockwiseRotationDegrees is 90 or 270;
        int outputWidth = swapsDimensions ? height : width;
        int outputHeight = swapsDimensions ? width : height;
        int requiredDestinationLength = checked(outputWidth * outputHeight);

        if (destination.Length < requiredDestinationLength)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        if (source.Overlaps(destination))
        {
            throw new ArgumentException("Source and destination buffers must not overlap.", nameof(destination));
        }

        for (int destinationY = 0; destinationY < outputHeight; destinationY++)
        {
            int destinationRow = destinationY * outputWidth;

            for (int destinationX = 0; destinationX < outputWidth; destinationX++)
            {
                (int sourceX, int sourceY) = clockwiseRotationDegrees switch
                {
                    0 => (destinationX, destinationY),
                    90 => (destinationY, height - 1 - destinationX),
                    180 => (width - 1 - destinationX, height - 1 - destinationY),
                    270 => (width - 1 - destinationY, destinationX),
                    _ => throw new InvalidOperationException()
                };

                destination[destinationRow + destinationX] =
                    source[sourceY * rowStride + sourceX * pixelStride];
            }
        }

        return new ImageInfo(outputWidth, outputHeight);
    }

    private static void ValidateSource(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int rowStride,
        int pixelStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelStride);

        int minimumRowStride = checked((width - 1) * pixelStride + 1);

        if (rowStride < minimumRowStride)
        {
            throw new ArgumentOutOfRangeException(nameof(rowStride));
        }

        int requiredSourceLength = checked((height - 1) * rowStride + minimumRowStride);

        if (source.Length < requiredSourceLength)
        {
            throw new ArgumentException("Source buffer is smaller than the declared plane.", nameof(source));
        }
    }

    private static void ValidateRotation(int rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), "Rotation must be 0, 90, 180, or 270 degrees.");
        }
    }
}
