namespace AliceVision.Core.Models;

/// <summary>
/// Original encoded texture and its validated dimensions. Disk models retain a
/// bounded lazy range reader, not every image's encoded or decoded bytes in RAM.
/// </summary>
public sealed class GltfImage
{
    private readonly Func<byte[]> _read;

    public GltfImage(byte[] bytes, string mimeType, int width, int height, int estimatedDecodedBytesPerPixel = 8)
        : this(bytes?.Length ?? throw new ArgumentNullException(nameof(bytes)), mimeType,
            width, height, () => (byte[])bytes.Clone(), estimatedDecodedBytesPerPixel) { }

    internal GltfImage(int encodedByteLength, string mimeType, int width, int height, Func<byte[]> read,
        int estimatedDecodedBytesPerPixel = 8)
    {
        if (encodedByteLength <= 0 || encodedByteLength > GltfDocument.MaximumEncodedImageBytes ||
            mimeType is not ("image/png" or "image/jpeg") || width <= 0 || height <= 0 ||
            width > GltfDocument.MaximumImageEdge || height > GltfDocument.MaximumImageEdge ||
            (long)width * height > GltfDocument.MaximumImagePixels || estimatedDecodedBytesPerPixel is not (4 or 8))
            throw new InvalidDataException("模型纹理大小、格式或尺寸无效。");
        EncodedByteLength = encodedByteLength;
        MimeType = mimeType;
        Width = width;
        Height = height;
        EstimatedDecodedBytesPerPixel = estimatedDecodedBytesPerPixel;
        _read = read ?? throw new ArgumentNullException(nameof(read));
    }

    public int EncodedByteLength { get; }
    public string MimeType { get; }
    public int Width { get; }
    public int Height { get; }
    public int EstimatedDecodedBytesPerPixel { get; }

    /// <summary>Reads the exact original encoded image once; no cache or recompression.</summary>
    public byte[] ReadEncodedBytes()
    {
        byte[] bytes = _read();
        if (bytes.Length != EncodedByteLength)
            throw new InvalidDataException("模型纹理数据长度发生变化，请重新导入。");
        return bytes;
    }
}
