using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using AliceVision.Core.Models;

if (args.Length == 2 && args[0] == "--inspect-model")
{
    try
    {
        GltfDocument model = GltfDocument.Load(args[1]);
        Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        int primitives = 0, vertices = 0, triangles = 0;
        var pending = new Stack<(int Node, Matrix4x4 Parent)>();
        foreach (int root in model.Roots) pending.Push((root, Matrix4x4.Identity));
        while (pending.TryPop(out var item))
        {
            GltfNode node = model.Nodes[item.Node];
            Matrix4x4 world = node.Transform * item.Parent;
            foreach (GltfPrimitive primitive in node.Primitives)
            {
                primitives++; vertices += primitive.Positions.Length / 3; triangles += primitive.Indices.Length / 3;
                for (int i = 0; i < primitive.Positions.Length; i += 3)
                {
                    Vector3 point = Vector3.Transform(new Vector3(primitive.Positions[i], primitive.Positions[i + 1], primitive.Positions[i + 2]), world);
                    minimum = Vector3.Min(minimum, point); maximum = Vector3.Max(maximum, point);
                }
                if (primitive.Skin is { } skin)
                {
                    minimum = Vector3.Min(minimum, skin.RestBoundsMinimum);
                    maximum = Vector3.Max(maximum, skin.RestBoundsMaximum);
                }
            }
            foreach (int child in node.Children) pending.Push((child, world));
        }
        Console.WriteLine($"Model parsed: {model.Nodes.Count} nodes, {primitives} primitives, {vertices} vertices, {triangles} triangles.");
        Console.WriteLine($"Embedded resources: {model.Images.Count} images, {model.Textures.Count} textures, {model.Materials.Count} materials.");
        Console.WriteLine($"World bounds minimum={minimum}, maximum={maximum}, size={maximum - minimum}.");
        Console.WriteLine($"Preserved skin={model.HasSkin}, humanoid bones={model.HumanBones.Count}, ignored morphs={model.HasMorphs}, ignored file animations={model.HasAnimations}.");
        Console.WriteLine("Humanoid mapping: " + string.Join(", ", model.HumanBones.Keys));
        return 0;
    }
    catch (InvalidDataException error)
    {
        Console.WriteLine("Model rejected: " + error.Message);
        return 1;
    }
}

var tests = new (string Name, Action Body)[]
{
    ("loads a packed triangle", LoadsTriangle),
    ("loads padded interleaved vertices", LoadsStridedVertices),
    ("preserves Y-up TRS and matrix translations", PreservesTransforms),
    ("preserves original bind vertices and native skin data", PreservesSkinData),
    ("validates legacy and modern VRM humanoid maps", ValidatesHumanBones),
    ("streams large sparse files without whole-file allocation", StreamsLargeFile),
    ("accepts exact 1 GiB boundary with bounded allocation", AcceptsExactFileBoundary),
    ("rejects files beyond 1 GiB before allocation", RejectsOversizedFile),
    ("streams component ranges across page boundaries", StreamsPageBoundary),
    ("loads embedded PNG and PBR material metadata", LoadsPngMaterial),
    ("accepts exporter identity texture transform but rejects actual transforms", IdentityTextureTransform),
    ("reports ignored morphs and animations", ReportsStaticPreview),
    ("rejects header version length and chunk corruption", RejectsHeaderCorruption),
    ("rejects every URI and compression extensions", RejectsExternalResources),
    ("rejects accessor offsets strides and huge counts", RejectsAccessorBounds),
    ("rejects invalid triangle indices and topology", RejectsInvalidIndices),
    ("rejects non-finite vertex and transform values", RejectsNonFinite),
    ("rejects cycles duplicate parents and missing nodes", RejectsInvalidHierarchy),
    ("rejects deep hierarchy even with sorted cached parents", RejectsDeepHierarchy),
    ("rejects sparse accessors", RejectsSparseAccessors),
    ("rejects bad skin indices weights and matrices", RejectsInvalidSkin),
    ("rejects oversized and corrupt image dimensions", RejectsImageBombs),
    ("accepts bounded multiple4MP metadata and rejects total decode budget", BoundsTextureSet),
    ("rejects duplicate JSON keys and external scenes", RejectsDuplicateKeys)
};
int failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine($"[PASS] {name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"[FAIL] {name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} model tests passed.");
return failures == 0 ? 0 : 1;

static void LoadsTriangle()
{
    var (json, binary) = Triangle();
    GltfDocument document = GltfDocument.Parse(Encode(json, binary));
    Assert(document.Nodes.Count == 1 && document.Roots.SequenceEqual(new[] { 0 }), "Scene roots changed.");
    GltfPrimitive primitive = document.Nodes[0].Primitives[0];
    Assert(primitive.Positions.SequenceEqual(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }), "Position bytes changed.");
    Assert(primitive.Indices.SequenceEqual(new uint[] { 0, 1, 2 }), "Triangle indices changed.");
    Assert(primitive.Normals is not null && primitive.Normals.SequenceEqual(new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }),
        "Missing normals should receive a bounded static-preview lighting fallback.");
    Assert(primitive.Material == -1 && !document.HasSkin, "Default metadata incorrect.");
}

static void LoadsStridedVertices()
{
    var (json, old) = Triangle();
    byte[] binary = new byte[50];
    for (int i = 0; i < 3; i++) old.AsSpan(i * 12, 12).CopyTo(binary.AsSpan(i * 16, 12));
    old.AsSpan(36, 6).CopyTo(binary.AsSpan(44, 6));
    json["buffers"]![0]!["byteLength"] = binary.Length;
    json["bufferViews"]![0]!["byteLength"] = 44;
    json["bufferViews"]![0]!["byteStride"] = 16;
    json["bufferViews"]![1]!["byteOffset"] = 44;
    var result = GltfDocument.Parse(Encode(json, binary));
    Assert(result.Nodes[0].Primitives[0].Positions[7] == 1, "Strided vertex mapping failed.");
}

static void PreservesTransforms()
{
    var (json, binary) = Triangle();
    json["nodes"]![0]!["translation"] = JsonNode.Parse("[1,2,3]");
    json["nodes"]![0]!["scale"] = JsonNode.Parse("[2,3,4]");
    var result = GltfDocument.Parse(Encode(json, binary));
    Vector3 point = Vector3.Transform(Vector3.One, result.Nodes[0].Transform);
    Assert(point == new Vector3(3, 5, 7), "TRS multiplication order changed.");
    json["nodes"]![0]!.AsObject().Remove("translation");
    json["nodes"]![0]!.AsObject().Remove("scale");
    json["nodes"]![0]!["matrix"] = JsonNode.Parse("[1,0,0,0,0,1,0,0,0,0,1,0,4,5,6,1]");
    result = GltfDocument.Parse(Encode(json, binary));
    Assert(Vector3.Transform(Vector3.Zero, result.Nodes[0].Transform) == new Vector3(4, 5, 6), "Column-major matrix translation moved.");
}

static void PreservesSkinData()
{
    var (json, binary) = SkinnedTriangle();
    var result = GltfDocument.Parse(Encode(json, binary));
    var primitive = result.Nodes[0].Primitives[0];
    Assert(result.HasSkin && primitive.Positions[1] == 0 && primitive.Skin is not null, "Original bind geometry or skin binding was lost.");
    var skin = primitive.Skin ?? throw new InvalidOperationException("Native skin binding missing.");
    Assert(skin.Joints.SequenceEqual(new[] { 1 }) && skin.Weights[0] == 1 && skin.BoneIndices[0] == 0,
        "Joint/weight buffers must remain usable for native skinning.");
    Assert(skin.RestBoundsMinimum.Y == 0 && skin.RestBoundsMaximum.Y == 1, "Bind-world bounds are incorrect.");
    json["nodes"]![1]!["translation"] = JsonNode.Parse("[0,3,0]");
    result = GltfDocument.Parse(Encode(json, binary));
    primitive = result.Nodes[0].Primitives[0];
    Assert(primitive.Positions[1] == 0 && primitive.Positions[7] == 1,
        "Native skinning must not receive CPU-baked vertices (double-transform risk).");
    Assert(primitive.Skin!.RestBoundsMinimum.Y == 1 && primitive.Skin.RestBoundsMaximum.Y == 2,
        "Native skin pose normalization must use evaluated rest-world bounds.");
}

static void ValidatesHumanBones()
{
    var (json, binary) = SkinnedTriangle();
    json["extensions"] = JsonNode.Parse("{\"VRM\":{\"humanoid\":{\"humanBones\":[{\"bone\":\"head\",\"node\":1}]}}}");
    Assert(GltfDocument.Parse(Encode(json, binary)).HumanBones["head"] == 1, "VRM0 humanBones map lost.");
    json["extensions"] = JsonNode.Parse("{\"VRMC_vrm\":{\"humanoid\":{\"humanBones\":{\"head\":{\"node\":1}}}}}");
    Assert(GltfDocument.Parse(Encode(json, binary)).HumanBones["head"] == 1, "VRM1 humanBones map lost.");
    json["extensions"]!["VRMC_vrm"]!["humanoid"]!["humanBones"]!["head"]!["node"] = 42;
    Reject(Encode(json, binary));
    json["extensions"] = JsonNode.Parse("{\"VRM\":{\"humanoid\":{\"humanBones\":[{\"bone\":\"head\",\"node\":1},{\"bone\":\"hips\",\"node\":1}]}}}");
    Reject(Encode(json, binary));
    json["extensions"] = JsonNode.Parse("{\"VRM\":{\"humanoid\":{\"humanBones\":[{\"bone\":\"unknown\",\"node\":1}]}}}");
    Reject(Encode(json, binary));
    json["extensions"] = JsonNode.Parse("{\"VRM\":{\"humanoid\":{\"humanBones\":[{\"bone\":\"head\",\"node\":1}]}}}");
    json["scenes"]![0]!["nodes"] = JsonNode.Parse("[0]");
    Reject(Encode(json, binary));
}

static void StreamsLargeFile()
{
    var (json, binary) = Triangle();
    string path = Path.Combine(Path.GetTempPath(), "AliceVision-stream-test-" + Guid.NewGuid().ToString("N") + ".glb");
    try
    {
        const int binLength = 96 * 1024 * 1024;
        WriteSparseGlb(path, json, binary, binLength);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = GltfDocument.Load(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(result.Nodes[0].Primitives[0].Positions[7] == 1, "Large GLB referenced data was not read.");
        Assert(allocated < 8 * 1024 * 1024, "Reader allocated a whole-file-sized buffer.");
    }
    finally { File.Delete(path); }
}

static void RejectsOversizedFile()
{
    string path = Path.Combine(Path.GetTempPath(), "AliceVision-limit-test-" + Guid.NewGuid().ToString("N") + ".glb");
    try
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength((long)GltfDocument.MaximumFileBytes + 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool rejected = false;
        try { _ = GltfDocument.Load(path); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected && GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "Oversized GLB must fail before a large allocation.");
    }
    finally { File.Delete(path); }
}

static void AcceptsExactFileBoundary()
{
    var (json, binary) = Triangle();
    string path = Path.Combine(Path.GetTempPath(), "AliceVision-exact-limit-test-" + Guid.NewGuid().ToString("N") + ".glb");
    try
    {
        json["buffers"]![0]!["byteLength"] = GltfDocument.MaximumFileBytes;
        int jsonLength = (Encoding.UTF8.GetByteCount(json.ToJsonString()) + 3) & ~3;
        WriteSparseGlb(path, json, binary, GltfDocument.MaximumFileBytes - 28 - jsonLength);
        Assert(new FileInfo(path).Length == GltfDocument.MaximumFileBytes, "Fixture must hit the exact accepted byte boundary.");
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert(GltfDocument.Load(path).Nodes[0].Primitives.Count == 1, "Exact 1 GiB boundary should load selected small ranges.");
        Assert(GC.GetAllocatedBytesForCurrentThread() - before < 8 * 1024 * 1024, "1 GiB file path allocated an unbounded buffer.");
    }
    finally { File.Delete(path); }
}

static void StreamsPageBoundary()
{
    var (json, original) = Triangle();
    byte[] binary = new byte[65532 + 42]; original.CopyTo(binary, 65532);
    json["bufferViews"]![0]!["byteOffset"] = 65532;
    json["bufferViews"]![1]!["byteOffset"] = 65532 + 36;
    string path = Path.Combine(Path.GetTempPath(), "AliceVision-page-test-" + Guid.NewGuid().ToString("N") + ".glb");
    try
    {
        WriteSparseGlb(path, json, binary, (binary.Length + 3) & ~3);
        Assert(GltfDocument.Load(path).Nodes[0].Primitives[0].Positions.SequenceEqual(new float[] { 0,0,0,1,0,0,0,1,0 }),
            "Sequential accessor read failed at the file page boundary.");
    }
    finally { File.Delete(path); }
}

static void WriteSparseGlb(string path, JsonObject json, byte[] binary, int binLength)
{
    json["buffers"]![0]!["byteLength"] = binLength;
    byte[] text = Encoding.UTF8.GetBytes(json.ToJsonString());
    int jsonLength = (text.Length + 3) & ~3;
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
    writer.Write(0x46546c67u); writer.Write(2u); writer.Write((uint)(28L + jsonLength + binLength));
    writer.Write((uint)jsonLength); writer.Write(0x4e4f534au); writer.Write(text);
    for (int i = text.Length; i < jsonLength; i++) writer.Write((byte)32);
    writer.Write((uint)binLength); writer.Write(0x004e4942u); writer.Write(binary);
    writer.Flush(); file.SetLength(28L + jsonLength + binLength);
}

static void LoadsPngMaterial()
{
    var (json, binary) = TexturedTriangle();
    var result = GltfDocument.Parse(Encode(json, binary));
    Assert(result.Images.Count == 1 && result.Images[0].Width == 1 && result.Images[0].Height == 1, "PNG dimensions lost.");
    Assert(result.Materials[0].BaseColor == new Vector4(0.5f, 0.75f, 1, 1) && result.Materials[0].AlphaMode == "MASK",
        "Material metadata changed.");
    Assert(result.Textures[0].WrapS == 33071, "Sampler wrap lost.");
}

static void ReportsStaticPreview()
{
    var (json, binary) = Triangle();
    json["meshes"]![0]!["primitives"]![0]!["targets"] = JsonNode.Parse("[{\"POSITION\":0}]");
    json["animations"] = JsonNode.Parse("[{\"channels\":[],\"samplers\":[]}]");
    var result = GltfDocument.Parse(Encode(json, binary));
    Assert(result.HasMorphs && result.HasAnimations, "Preview limitations were not reported.");
    Assert(result.Nodes[0].Primitives[0].Positions[7] == 1, "Ignored morph changed base mesh.");
}

static void IdentityTextureTransform()
{
    var (json, binary) = TexturedTriangle();
    json["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]!["extensions"] =
        JsonNode.Parse("{\"KHR_texture_transform\":{\"offset\":[0,0],\"scale\":[1,1]}}");
    Assert(GltfDocument.Parse(Encode(json, binary)).Textures.Count == 1, "No-op exporter texture extension was rejected.");
    json["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"]!["extensions"]!["KHR_texture_transform"]!["offset"] = JsonNode.Parse("[0.5,0]");
    Reject(Encode(json, binary));
}

static void RejectsHeaderCorruption()
{
    var (json, binary) = Triangle();
    byte[] valid = Encode(json, binary);
    foreach (int field in new[] { 0, 4, 8, 12, 16 })
    {
        byte[] bad = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(field, 4), uint.MaxValue);
        Reject(bad);
    }
    Reject(valid[..^1]);
    Reject(new byte[28]);
}

static void RejectsExternalResources()
{
    var (json, binary) = Triangle();
    foreach (string uri in new[] { "https://example.com/model.bin", "../secret", "file:///tmp/a", "data:application/octet-stream;base64,AAAA" })
    {
        json["buffers"]![0]!["uri"] = uri;
        Reject(Encode(json, binary));
    }
    json["buffers"]![0]!.AsObject().Remove("uri");
    json["extensionsRequired"] = JsonNode.Parse("[\"KHR_draco_mesh_compression\"]");
    Reject(Encode(json, binary));
    json.AsObject().Remove("extensionsRequired");
    json["bufferViews"]![0]!["extensions"] = JsonNode.Parse("{\"EXT_meshopt_compression\":{}}");
    Reject(Encode(json, binary));
}

static void RejectsAccessorBounds()
{
    var (json, binary) = Triangle();
    json["accessors"]![0]!["byteOffset"] = 4;
    Reject(Encode(json, binary));
    json["accessors"]![0]!["byteOffset"] = 0;
    json["accessors"]![0]!["count"] = int.MaxValue;
    Reject(Encode(json, binary));
    json["accessors"]![0]!["count"] = 3;
    json["bufferViews"]![0]!["byteStride"] = 7;
    Reject(Encode(json, binary));
    json["bufferViews"]![0]!.AsObject().Remove("byteStride");
    json["bufferViews"]![0]!["byteLength"] = 35;
    Reject(Encode(json, binary));
}

static void RejectsInvalidIndices()
{
    var (json, binary) = Triangle();
    BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(40, 2), 99);
    Reject(Encode(json, binary));
    BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(40, 2), 2);
    json["meshes"]![0]!["primitives"]![0]!["mode"] = 5;
    Reject(Encode(json, binary));
}

static void RejectsNonFinite()
{
    var (json, binary) = Triangle();
    foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
    {
        BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(0, 4), BitConverter.SingleToInt32Bits(value));
        Reject(Encode(json, binary));
    }
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(0, 4), 0);
    json["nodes"]![0]!["translation"] = JsonNode.Parse("[1e40,0,0]");
    Reject(Encode(json, binary));
}

static void RejectsInvalidHierarchy()
{
    var (json, binary) = Triangle();
    json["nodes"] = JsonNode.Parse("[{\"mesh\":0,\"children\":[1]},{\"children\":[0]}]");
    Reject(Encode(json, binary));
    json["nodes"] = JsonNode.Parse("[{\"mesh\":0},{\"children\":[0]},{\"children\":[0]}]");
    Reject(Encode(json, binary));
    json["nodes"] = JsonNode.Parse("[{\"mesh\":0,\"children\":[42]}]");
    Reject(Encode(json, binary));
    json["nodes"] = JsonNode.Parse("[{\"mesh\":0}]");
    json["scenes"]![0]!["nodes"] = JsonNode.Parse("[]");
    Reject(Encode(json, binary));
}

static void RejectsSparseAccessors()
{
    var (json, binary) = Triangle();
    json["accessors"]![0]!["sparse"] = JsonNode.Parse("{\"count\":1}");
    Reject(Encode(json, binary));
}

static void RejectsDeepHierarchy()
{
    var (json, binary) = Triangle();
    var nodes = new JsonArray();
    for (int i = 0; i < 130; i++)
    {
        var node = new JsonObject();
        if (i < 129) node["children"] = new JsonArray(i + 1);
        else node["mesh"] = 0;
        nodes.Add(node);
    }
    json["nodes"] = nodes;
    Reject(Encode(json, binary));
}

static void RejectsInvalidSkin()
{
    var (json, binary) = SkinnedTriangle();
    binary[44] = 1;
    Reject(Encode(json, binary));
    binary[44] = 0;
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(56, 4), BitConverter.SingleToInt32Bits(-1));
    Reject(Encode(json, binary));
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(56, 4), BitConverter.SingleToInt32Bits(1));
    json["skins"]![0]!["joints"] = JsonNode.Parse("[42]");
    Reject(Encode(json, binary));
}

static void RejectsImageBombs()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 100_000);
    Reject(Encode(json, binary));
    binary[44] = 0;
    Reject(Encode(json, binary));
}

static void RejectsDuplicateKeys()
{
    var (json, binary) = Triangle();
    string duplicate = json.ToJsonString().Replace("\"mesh\":0", "\"mesh\":0,\"mesh\":0", StringComparison.Ordinal);
    Reject(EncodeJson(duplicate, binary));
    json["scenes"]![0]!["nodes"] = JsonNode.Parse("[99]");
    Reject(Encode(json, binary));
}

static void BoundsTextureSet()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 2048);
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 20, 4), 2048);
    // This test exercises header budgets, not native PNG decoding. Each reference
    // is counted because the importer could create a separate decoded image.
    var images = new JsonArray();
    for (int i = 0; i < 11; i++) images.Add(json["images"]![0]!.DeepClone());
    json["images"] = images;
    Assert(GltfDocument.Parse(Encode(json, binary)).Images.Count == 11, "A bounded 4MP texture set was rejected.");
    while (images.Count < 17) images.Add(images[0]!.DeepClone());
    Reject(Encode(json, binary));
}

static (JsonObject Json, byte[] Binary) Triangle()
{
    JsonObject json = JsonNode.Parse("""
        {"asset":{"version":"2.0"},"buffers":[{"byteLength":42}],
        "bufferViews":[{"buffer":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":6}],
        "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
        {"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
        "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
        "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
        """)!.AsObject();
    byte[] binary = new byte[42];
    float[] positions = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
    Buffer.BlockCopy(positions, 0, binary, 0, 36);
    for (int i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(36 + i * 2, 2), (ushort)i);
    return (json, binary);
}

static (JsonObject Json, byte[] Binary) SkinnedTriangle()
{
    var (json, original) = Triangle();
    byte[] binary = new byte[168]; original.CopyTo(binary, 0);
    json["buffers"]![0]!["byteLength"] = binary.Length;
    var views = json["bufferViews"]!.AsArray();
    views.Add(JsonNode.Parse("{\"buffer\":0,\"byteOffset\":44,\"byteLength\":12}"));
    views.Add(JsonNode.Parse("{\"buffer\":0,\"byteOffset\":56,\"byteLength\":48}"));
    views.Add(JsonNode.Parse("{\"buffer\":0,\"byteOffset\":104,\"byteLength\":64}"));
    var accessors = json["accessors"]!.AsArray();
    accessors.Add(JsonNode.Parse("{\"bufferView\":2,\"componentType\":5121,\"count\":3,\"type\":\"VEC4\"}"));
    accessors.Add(JsonNode.Parse("{\"bufferView\":3,\"componentType\":5126,\"count\":3,\"type\":\"VEC4\"}"));
    accessors.Add(JsonNode.Parse("{\"bufferView\":4,\"componentType\":5126,\"count\":1,\"type\":\"MAT4\"}"));
    json["meshes"]![0]!["primitives"]![0]!["attributes"]!["JOINTS_0"] = 2;
    json["meshes"]![0]!["primitives"]![0]!["attributes"]!["WEIGHTS_0"] = 3;
    json["nodes"] = JsonNode.Parse("[{\"mesh\":0,\"skin\":0},{\"translation\":[0,2,0]}]");
    json["skins"] = JsonNode.Parse("[{\"joints\":[1],\"inverseBindMatrices\":4}]");
    json["scenes"]![0]!["nodes"] = JsonNode.Parse("[0,1]");
    float[] weights = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };
    Buffer.BlockCopy(weights, 0, binary, 56, 48);
    float[] inverse = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -2, 0, 1 };
    Buffer.BlockCopy(inverse, 0, binary, 104, 64);
    return (json, binary);
}

static (JsonObject Json, byte[] Binary) TexturedTriangle()
{
    var (json, original) = Triangle();
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
    byte[] binary = new byte[44 + png.Length]; original.CopyTo(binary, 0); png.CopyTo(binary, 44);
    json["buffers"]![0]!["byteLength"] = binary.Length;
    json["bufferViews"]!.AsArray().Add(JsonNode.Parse($"{{\"buffer\":0,\"byteOffset\":44,\"byteLength\":{png.Length}}}"));
    json["images"] = JsonNode.Parse("[{\"bufferView\":2,\"mimeType\":\"image/png\"}]");
    json["samplers"] = JsonNode.Parse("[{\"wrapS\":33071,\"wrapT\":33648}]");
    json["textures"] = JsonNode.Parse("[{\"source\":0,\"sampler\":0}]");
    json["materials"] = JsonNode.Parse("[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.5,0.75,1,1],\"baseColorTexture\":{\"index\":0}},\"alphaMode\":\"MASK\"}]");
    json["meshes"]![0]!["primitives"]![0]!["material"] = 0;
    return (json, binary);
}

static byte[] Encode(JsonObject json, byte[] binary) => EncodeJson(json.ToJsonString(), binary);
static byte[] EncodeJson(string json, byte[] binary)
{
    byte[] text = Encoding.UTF8.GetBytes(json);
    int jsonLength = (text.Length + 3) & ~3, binaryLength = (binary.Length + 3) & ~3;
    byte[] bytes = new byte[12 + 8 + jsonLength + 8 + binaryLength];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x46546c67);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)bytes.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), (uint)jsonLength);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 0x4e4f534a);
    bytes.AsSpan(20, jsonLength).Fill(32); text.CopyTo(bytes, 20);
    int offset = 20 + jsonLength;
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), (uint)binaryLength);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4, 4), 0x004e4942);
    binary.CopyTo(bytes, offset + 8);
    return bytes;
}

static void Reject(byte[] bytes)
{
    try { _ = GltfDocument.Parse(bytes); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Malformed model was not rejected safely.");
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
