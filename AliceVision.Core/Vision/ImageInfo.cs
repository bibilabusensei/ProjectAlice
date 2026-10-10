namespace AliceVision.Core.Vision;

/// <summary>
/// Describes the dimensions of a packed image supplied to the vision pipeline.
/// </summary>
public readonly record struct ImageInfo
{
    public ImageInfo(int width, int height, int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        Width = width;
        Height = height;
        Channels = channels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Channels { get; }

    public long PixelCount => (long)Width * Height;
}
