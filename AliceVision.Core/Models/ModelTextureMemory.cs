namespace AliceVision.Core.Models;

/// <summary>
/// Conservative additional memory estimate for original-resolution textures.
/// Callers supply unique referenced images only; unreferenced thumbnails cost
/// no decode or GPU budget. This is a preflight guard, not an OS memory guarantee.
/// </summary>
public static class ModelTextureMemory
{
    public const long GeometryReserveBytes = 32L * 1024 * 1024;

    // Headers distinguish ordinary RGBA8 output from high-depth RGBA16. Unknown
    // custom images conservatively default to 8 bytes per pixel, not a downscale.
    public static long DecodedBytes(GltfImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return checked((long)image.Width * image.Height * image.EstimatedDecodedBytesPerPixel);
    }

    public static long TextureResidentBytes(GltfImage image)
    {
        long decoded = DecodedBytes(image);
        // A 4/3 mip multiplier is not conservative for very narrow textures.
        // Sum actual levels through 1x1 without changing the authored dimensions.
        long mipPixels = 0;
        int width = image.Width, height = image.Height;
        while (true)
        {
            mipPixels = checked(mipPixels + (long)width * height);
            if (width == 1 && height == 1) break;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        // A decoded CGImage provider may retain its native encoded NSData even
        // after the source is disposed. Include that copy until scene disposal.
        return checked(decoded + mipPixels * image.EstimatedDecodedBytesPerPixel + image.EncodedByteLength);
    }

    public static long Estimate(IEnumerable<GltfImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        long resident = 0, largestTemporary = 0;
        int count = 0;
        foreach (GltfImage image in images)
        {
            if (++count > GltfDocument.MaximumImages)
                throw new InvalidDataException("需要加载的纹理数量超过安全限制。");
            resident = checked(resident + TextureResidentBytes(image));
            // Sequential decode still needs encoded managed bytes + native
            // NSData copy, and an extra source-sized decoder scratch buffer.
            long temporary = checked(DecodedBytes(image) + 2L * image.EncodedByteLength);
            largestTemporary = Math.Max(largestTemporary, temporary);
        }
        return checked(resident + largestTemporary + GeometryReserveBytes);
    }
}
