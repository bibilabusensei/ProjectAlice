namespace AliceVision.Core.Models;

/// <summary>
/// Conservative peak memory estimate for unchanged geometry. Counts must be
/// validated before allocating vertex, index or skin buffers. This deliberately
/// counts managed data, native SceneKit CPU storage and GPU storage separately,
/// even though iPhone unified memory may share some of those allocations. It is
/// an admission check, not a guarantee against operating-system memory pressure.
/// </summary>
public sealed record GltfGeometryMemory(long ManagedBytes, long NativeCpuBytes, long GpuBytes,
    long LargestTransientBytes)
{
    public const long MetadataReserveBytes = 32L * 1024 * 1024;
    public long TotalBytes => checked(ManagedBytes + NativeCpuBytes + GpuBytes + LargestTransientBytes);

    public static GltfGeometryMemory Estimate(long vertices, long triangles, long skinnedVertices,
        int maxPrimitiveVertices = 0, int maxPrimitiveTriangles = 0, long skinJointReferences = 0)
    {
        if (vertices < 0 || vertices > GltfDocument.MaximumVertices ||
            triangles < 0 || triangles > GltfDocument.MaximumTriangles ||
            skinnedVertices < 0 || skinnedVertices > vertices ||
            maxPrimitiveVertices < 0 || maxPrimitiveVertices > vertices ||
            maxPrimitiveTriangles < 0 || maxPrimitiveTriangles > triangles ||
            skinJointReferences < 0 || skinJointReferences > GltfDocument.MaximumSkinJoints * 2048L ||
            (vertices == 0 && triangles != 0))
            throw new InvalidDataException("模型几何内存估计的顶点、三角形或蒙皮计数无效。");

        // A caller without primitive maxima gets a whole-model scratch estimate,
        // not a misleading zero-byte transient allowance. Normals and UV buffers
        // are conservatively included even if the original primitive omits them.
        long largestVertices = maxPrimitiveVertices == 0 ? vertices : maxPrimitiveVertices;
        long largestTriangles = maxPrimitiveTriangles == 0 ? triangles : maxPrimitiveTriangles;
        long data = checked(vertices * 32 + skinnedVertices * 24 + triangles * 12);
        // SceneKit construction temporarily holds native-upload byte copies and
        // the UV-orientation float clone alongside the original arrays. Leave
        // room for those copies before a collection reclaims transient buffers.
        long transient = checked(largestVertices * 64 + largestTriangles * 24 + 1024 * 1024);
        // A tiny primitive may still reference a large skeleton. Count palettes
        // per primitive, not just unique bones, because both native Skinner and
        // managed bindings retain their own lists/matrices. The 128-byte CPU
        // allowance includes each 64-byte bind matrix and associated wrappers.
        long cpu = checked(data + skinJointReferences * 128);
        long gpu = checked(data + skinJointReferences * 64);
        return new GltfGeometryMemory(checked(cpu + MetadataReserveBytes), cpu, gpu, transient);
    }

    /// <summary>
    /// Estimate native CPU copies for a validated primitive, so a sequential
    /// importer can subtract completed uploads from its remaining peak estimate.
    /// Skin node references/matrices are counted without altering authored data.
    /// </summary>
    public static long NativeCpuBytesFor(GltfPrimitive primitive)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        long bytes = checked((long)primitive.Positions.Length * sizeof(float) +
            (long)(primitive.Normals?.Length ?? 0) * sizeof(float) +
            (long)(primitive.Texcoords?.Length ?? 0) * sizeof(float) +
            (long)primitive.Indices.Length * sizeof(uint));
        if (primitive.Skin is { } skin)
            bytes = checked(bytes + (long)skin.Weights.Length * sizeof(float) +
                (long)skin.BoneIndices.Length * sizeof(ushort) +
                (long)skin.Joints.Length * 128);
        return bytes;
    }
}
