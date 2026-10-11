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
    ("converts Numerics transforms to native columns without projective translation", ConvertsSceneColumns),
    ("preserves scaled rotated parent chains and inverse bind transforms", PreservesSceneSkinColumns),
    ("rejects nonfinite native column transformations", BoundsSceneColumns),
    ("preserves original bind vertices and native skin data", PreservesSkinData),
    ("preserves 307-joint and 1024-joint rigs with unsigned short influences", PreservesLargeSkinData),
    ("rejects oversized or invalid large-rig joints with actionable diagnostics", BoundsLargeSkinData),
    ("identifies non-array model fields and unchanged image count bounds", DiagnosesArrayBounds),
    ("estimates large unchanged geometry without arithmetic overflow", EstimatesGeometryMemory),
    ("bounds geometry estimates and accounts for native skin buffers", BoundsGeometryMemory),
    ("runs streamed geometry preflight before reading vertex arrays", RunsGeometryPreflight),
    ("preserves native memory guard rejection type and message", PreservesPreflightRejection),
    ("supports cancellation before streamed geometry allocation", CancelsGeometryPreflight),
    ("reads only the used inverse bind prefix of oversized accessors", BoundsInverseBindPrefix),
    ("validates legacy and modern VRM humanoid maps", ValidatesHumanBones),
    ("streams large sparse files without whole-file allocation", StreamsLargeFile),
    ("accepts exact 1 GiB boundary with bounded allocation", AcceptsExactFileBoundary),
    ("rejects files beyond 1 GiB before allocation", RejectsOversizedFile),
    ("streams component ranges across page boundaries", StreamsPageBoundary),
    ("loads embedded PNG and PBR material metadata", LoadsPngMaterial),
    ("preserves glTF emissive factor texture strength and zero defaults", PreservesEmission),
    ("rejects invalid emissive factors strengths and texture references", BoundsEmission),
    ("validates emissive texture coordinates and identity transforms", ValidatesEmissionTexture),
    ("accounts for unique emission-only images before lazy texture decoding", AccountsEmissionImages),
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
    ("accepts 5 10 and 64 original-resolution 4K image metadata", AcceptsLargeTextureSets),
    ("accepts full 8K metadata but rejects image count and dimension bombs", BoundsTextureMetadata),
    ("reads original texture bytes lazily after the parsing stream closes", PreservesLazyTextureBytes),
    ("refuses lazy texture reads after source timestamp or length changes", RejectsChangedTextureSource),
    ("bounds metadata allocation independently of encoded image size", BoundsImageMetadataAllocation),
    ("rejects per-image and total encoded texture limits before allocation", BoundsEncodedTextureBytes),
    ("estimates original resolution CPU GPU mips and decode scratch", EstimatesTextureMemory),
    ("retains 8-bit and 16-bit precision in metadata memory estimates", EstimatesTexturePrecision),
    ("ignores unreferenced images when estimating chosen textures", EstimatesReferencedTextures),
    ("rejects malformed memory estimator inputs", RejectsTextureMemoryInputs),
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

static Vector4 NativeColumnTransform(Vector4 point, GltfSceneTransform matrix) =>
    matrix.Column0 * point.X + matrix.Column1 * point.Y + matrix.Column2 * point.Z + matrix.Column3 * point.W;

static void AssertColumnTransform(Matrix4x4 matrix, Vector4 value, string message)
{
    Vector4 expected = Vector4.Transform(value, matrix);
    Vector4 native = NativeColumnTransform(value, GltfSceneTransform.FromNumerics(matrix));
    Assert(Vector4.Distance(expected, native) < 0.0001f, message);
}

static void ConvertsSceneColumns()
{
    var source = Matrix4x4.CreateTranslation(2, -3, 5);
    var columns = GltfSceneTransform.FromNumerics(source);
    Assert(columns.Column3 == new Vector4(2, -3, 5, 1) && columns.Column0.W == 0 && columns.Column1.W == 0 && columns.Column2.W == 0,
        "Translation must occupy the native final column, never the perspective row.");
    AssertColumnTransform(source, new Vector4(1, 2, 3, 1), "Native column translation differs from Numerics point translation.");
    AssertColumnTransform(source, new Vector4(1, 2, 3, 0), "Native column translation changed a direction vector.");

    // The former scalar-constructor copy interpreted Numerics source rows as
    // native rows. Transposing those into columns reproduces the faulty bridge.
    var formerColumns = new GltfSceneTransform(
        new Vector4(source.M11, source.M21, source.M31, source.M41),
        new Vector4(source.M12, source.M22, source.M32, source.M42),
        new Vector4(source.M13, source.M23, source.M33, source.M43),
        new Vector4(source.M14, source.M24, source.M34, source.M44));
    Vector4 wrong = NativeColumnTransform(new Vector4(1, 2, 3, 1), formerColumns);
    Assert(wrong.W != 1 && Vector4.Distance(wrong, new Vector4(3, -1, 8, 1)) > 1,
        "Synthetic translated-bone counterexample no longer detects the old perspective-row corruption.");
}

static void PreservesSceneSkinColumns()
{
    var rotation = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(0.31f, -0.48f, 0.27f));
    var local = Matrix4x4.CreateScale(2, 0.5f, 1.5f) * rotation * Matrix4x4.CreateTranslation(0.2f, 1.7f, -0.4f);
    var parent = Matrix4x4.CreateRotationX(-0.6f) * Matrix4x4.CreateTranslation(2.1f, 0.3f, -1.2f);
    var world = local * parent;
    var point = new Vector4(0.35f, 0.7f, -0.2f, 1);
    var direction = new Vector4(-0.7f, 0.2f, 0.9f, 0);
    AssertColumnTransform(local, point, "Native scaled-quaternion local point transform changed.");
    AssertColumnTransform(local, direction, "Native scaled-quaternion local direction transform changed.");
    AssertColumnTransform(world, point, "Native parent-chain point transform changed.");
    Vector4 nested = NativeColumnTransform(NativeColumnTransform(point, GltfSceneTransform.FromNumerics(local)),
        GltfSceneTransform.FromNumerics(parent));
    Assert(Vector4.Distance(nested, Vector4.Transform(point, world)) < 0.0001f,
        "Native column multiplication order did not preserve child-then-parent transforms.");
    Assert(Matrix4x4.Invert(world, out Matrix4x4 inverseBind), "Synthetic bind transform must be invertible.");
    Vector4 bindThenJoint = NativeColumnTransform(NativeColumnTransform(point, GltfSceneTransform.FromNumerics(inverseBind)),
        GltfSceneTransform.FromNumerics(world));
    Assert(Vector4.Distance(bindThenJoint, point) < 0.0001f,
        "Inverse bind and joint world columns did not cancel in the unchanged bind pose.");
    AssertColumnTransform(inverseBind, point, "Native inverse-bind point transform changed.");
    AssertColumnTransform(inverseBind, direction, "Native inverse-bind direction transform changed.");
}

static void BoundsSceneColumns()
{
    foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
    {
        var matrix = Matrix4x4.Identity;
        matrix.M41 = invalid;
        bool rejected = false;
        try { _ = GltfSceneTransform.FromNumerics(matrix); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Nonfinite matrix reached native column storage.");
    }
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

static void PreservesLargeSkinData()
{
    // This fixture is authored here, not copied from a user's avatar. Its high
    // joint indices exercise the real 16-bit path without large mesh allocation.
    foreach (int jointCount in new[] { 307, 1024 })
    {
        var (json, binary) = ManyJointTriangle(jointCount);
        var result = GltfDocument.Parse(Encode(json, binary));
        var primitive = result.Nodes[0].Primitives[0];
        var skin = primitive.Skin ?? throw new InvalidOperationException("Large-rig skin was lost.");
        Assert(skin.Joints.Length == jointCount && skin.InverseBindMatrices.Length == jointCount,
            "Large-rig joint list or inverse bind matrices were truncated.");
        Assert(skin.InverseBindMatrices.All(matrix => matrix == Matrix4x4.Identity),
            "Large-rig authored inverse bind matrices changed.");
        Assert(skin.Joints.SequenceEqual(Enumerable.Range(1, jointCount)), "Large-rig joint order changed.");
        Assert(skin.BoneIndices[0] == 0 && skin.BoneIndices[4] == 256 && skin.BoneIndices[8] == jointCount - 1,
            "Unsigned short bone indices above 255 were truncated or remapped.");
        Assert(skin.Weights[0] == 1 && skin.Weights[4] == 1 && skin.Weights[8] == 1,
            "Large-rig skin weights changed.");
        Assert(primitive.Positions.SequenceEqual(new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) &&
            skin.RestBoundsMinimum == Vector3.Zero && skin.RestBoundsMaximum == new Vector3(1, 1, 0),
            "Large-rig import changed authored geometry or identity bind pose.");
    }
}

static void BoundsLargeSkinData()
{
    var (json, binary) = ManyJointTriangle(1025);
    string message = RejectionMessage(Encode(json, binary));
    Assert(message.Contains("joints", StringComparison.Ordinal) && message.Contains("1025", StringComparison.Ordinal) &&
        message.Contains("1024", StringComparison.Ordinal), "Large-rig count error lacks field, actual count or limit.");

    (json, binary) = ManyJointTriangle(307);
    BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(44 + 2 * 8, 2), 307);
    Reject(Encode(json, binary));

    (json, binary) = ManyJointTriangle(307);
    json["skins"]![0]!["joints"]![306] = 1;
    Reject(Encode(json, binary));
}

static void DiagnosesArrayBounds()
{
    var (json, binary) = ManyJointTriangle(307);
    json["skins"]![0]!["joints"] = new JsonObject();
    string message = RejectionMessage(Encode(json, binary));
    Assert(message.Contains("joints", StringComparison.Ordinal) && message.Contains("Object", StringComparison.Ordinal),
        "Non-array error should identify the field and actual JSON kind.");

    (json, binary) = TexturedTriangle();
    var images = json["images"]!.AsArray();
    for (int i = images.Count; i < 65; i++) images.Add(images[0]!.DeepClone());
    message = RejectionMessage(Encode(json, binary));
    Assert(message.Contains("images", StringComparison.Ordinal) && message.Contains("65", StringComparison.Ordinal) &&
        message.Contains("64", StringComparison.Ordinal), "Image count safety bound was lifted or lost its diagnostic.");
}

static void ValidatesHumanBones()
{
    var (json, binary) = SkinnedTriangle();
    json["extensions"] = JsonNode.Parse("{\"VRM\":{\"humanoid\":{\"humanBones\":[{\"bone\":\"head\",\"node\":1}]}}}");
    var legacy = GltfDocument.Parse(Encode(json, binary));
    Assert(legacy.HumanBones["head"] == 1 && legacy.IsLegacyVrm, "VRM0 humanBones/version lost.");
    Assert(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ, legacy.AvatarBasis), Vector3.UnitZ) < 0.00001f,
        "Legacy avatar must be normalized to +Z forward, including meshes and joints together.");
    json["extensions"] = JsonNode.Parse("{\"VRMC_vrm\":{\"humanoid\":{\"humanBones\":{\"head\":{\"node\":1}}}}}");
    var modern = GltfDocument.Parse(Encode(json, binary));
    Assert(modern.HumanBones["head"] == 1 && !modern.IsLegacyVrm && modern.AvatarBasis == Matrix4x4.Identity,
        "VRM1 must preserve its +Z convention.");
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

static void PreservesEmission()
{
    var (json, binary) = TexturedTriangle();
    GltfMaterial original = GltfDocument.Parse(Encode(json, binary)).Materials[0];
    Assert(original.EmissiveFactor == Vector3.Zero && original.EmissiveTexture == -1 &&
        original.EmissiveStrength == 1 && !original.HasEmission, "Omitted emission must remain off.");
    json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("[0.25,0.5,1]");
    json["materials"]![0]!["emissiveTexture"] = JsonNode.Parse("{\"index\":0}");
    json["materials"]![0]!["extensions"] = JsonNode.Parse("{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":3.5}}");
    json["extensionsRequired"] = JsonNode.Parse("[\"KHR_materials_emissive_strength\"]");
    GltfMaterial result = GltfDocument.Parse(Encode(json, binary)).Materials[0];
    Assert(result.EmissiveFactor == new Vector3(0.25f, 0.5f, 1) && result.EmissiveTexture == 0 &&
        result.EmissiveStrength == 3.5f && result.HasEmission, "Authored emission metadata was changed or clamped.");
    json["materials"]![0]!["extensions"]!["KHR_materials_emissive_strength"]!["emissiveStrength"] = 0;
    Assert(!GltfDocument.Parse(Encode(json, binary)).Materials[0].HasEmission, "Zero strength must disable emission.");
}

static void BoundsEmission()
{
    foreach (string invalid in new[] { "[-0.1,0,0]", "[0,1.1,0]", "[1,1]", "[0,0,1e100]" })
    {
        var (json, binary) = TexturedTriangle();
        json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse(invalid);
        Reject(Encode(json, binary));
    }
    foreach (string invalid in new[] { "-1", "1e100", "\"unexpected\"" })
    {
        var (json, binary) = TexturedTriangle();
        json["materials"]![0]!["extensions"] = JsonNode.Parse("{\"KHR_materials_emissive_strength\":{\"emissiveStrength\":" + invalid + "}}");
        Reject(Encode(json, binary));
    }
    foreach (int invalid in new[] { -1, 1, int.MaxValue })
    {
        var (json, binary) = TexturedTriangle();
        json["materials"]![0]!["emissiveTexture"] = JsonNode.Parse("{\"index\":" + invalid + "}");
        Reject(Encode(json, binary));
    }
}

static void ValidatesEmissionTexture()
{
    var (json, binary) = TexturedTriangle();
    json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("[1,1,1]");
    json["materials"]![0]!["emissiveTexture"] = JsonNode.Parse("{\"index\":0,\"texCoord\":0,\"extensions\":{\"KHR_texture_transform\":{\"offset\":[0,0],\"scale\":[1,1]}}}");
    Assert(GltfDocument.Parse(Encode(json, binary)).Materials[0].HasEmission, "Identity emissive transform rejected.");
    json["materials"]![0]!["emissiveTexture"]!["texCoord"] = 1;
    Reject(Encode(json, binary));
    json["materials"]![0]!["emissiveTexture"]!["texCoord"] = 0;
    json["materials"]![0]!["emissiveTexture"]!["extensions"]!["KHR_texture_transform"]!["scale"] = JsonNode.Parse("[2,1]");
    Reject(Encode(json, binary));
}

static void AccountsEmissionImages()
{
    var (json, binary) = TexturedTriangle();
    json["images"]!.AsArray().Add(json["images"]![0]!.DeepClone());
    json["images"]!.AsArray().Add(json["images"]![0]!.DeepClone()); // Unused thumbnail.
    json["textures"]!.AsArray().Add(JsonNode.Parse("{\"source\":1}"));
    json["textures"]!.AsArray().Add(JsonNode.Parse("{\"source\":0}")); // Alias, not another upload.
    json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("[1,1,1]");
    json["materials"]![0]!["emissiveTexture"] = JsonNode.Parse("{\"index\":1}");
    var document = GltfDocument.Parse(Encode(json, binary));
    var referenced = document.ReferencedImageIndices(new[] { 0, 0 });
    Assert(referenced.SetEquals(new[] { 0, 1 }), "Emission-only pixels were not admitted exactly once.");
    long selected = ModelTextureMemory.Estimate(referenced.Select(index => document.Images[index]));
    Assert(selected > ModelTextureMemory.Estimate(new[] { document.Images[0] }) &&
        selected < ModelTextureMemory.Estimate(document.Images), "Emission texture bypassed or inflated memory admission.");
    Assert(document.Images[1].ReadEncodedBytes().SequenceEqual(document.Images[0].ReadEncodedBytes()),
        "Emission texture original bytes changed.");
    json["materials"]![0]!["emissiveTexture"]!["index"] = 2;
    Assert(GltfDocument.Parse(Encode(json, binary)).ReferencedImageIndices(new[] { 0 }).SetEquals(new[] { 0 }),
        "Shared diffuse/emission image was uploaded twice.");
    json["materials"]![0]!["emissiveTexture"]!["index"] = 1;
    json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("[0,0,0]");
    Assert(GltfDocument.Parse(Encode(json, binary)).ReferencedImageIndices(new[] { 0 }).SetEquals(new[] { 0 }),
        "Invisible emission texture entered decode budget.");
    json["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("[1,1,1]");
    json["materials"]![0]!["extensions"] = JsonNode.Parse("{\"KHR_materials_unlit\":{}}");
    Assert(GltfDocument.Parse(Encode(json, binary)).ReferencedImageIndices(new[] { 0 }).SetEquals(new[] { 0 }),
        "Unlit material must not apply emission.");
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

static void AcceptsLargeTextureSets()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 4096);
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 20, 4), 4096);
    // These are metadata-only fixtures, not full 4K encoded PNGs. Rendering tests
    // require a real device; metadata must not reject 5+ original 4K images.
    var images = new JsonArray();
    JsonNode image = json["images"]![0]!.DeepClone();
    json["images"] = images;
    foreach (int target in new[] { 5, 10, 64 })
    {
        while (images.Count < target) images.Add(image.DeepClone());
        var parsed = GltfDocument.Parse(Encode(json, binary));
        Assert(parsed.Images.Count == target && parsed.Images.All(texture => texture.Width == 4096 && texture.Height == 4096),
            "4K metadata was rejected or silently reduced.");
    }
}

static void BoundsTextureMetadata()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 8192);
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 20, 4), 8192);
    Assert(GltfDocument.Parse(Encode(json, binary)).Images[0].Width == 8192, "Exact 8K boundary must retain original dimensions.");
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 8193);
    Reject(Encode(json, binary));
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 0);
    Reject(Encode(json, binary));
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), uint.MaxValue);
    Reject(Encode(json, binary));
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 1);
    var images = new JsonArray();
    for (int i = 0; i < 65; i++) images.Add(json["images"]![0]!.DeepClone());
    json["images"] = images;
    Reject(Encode(json, binary));
}

static void PreservesLazyTextureBytes()
{
    var (json, binary) = TexturedTriangle();
    string path = TemporaryModelPath();
    try
    {
        WriteSparseGlb(path, json, binary, (binary.Length + 3) & ~3);
        GltfDocument result = GltfDocument.Load(path);
        byte[] expected = binary[44..];
        Assert(result.Images[0].EncodedByteLength == expected.Length, "Encoded texture length changed.");
        byte[] first = result.Images[0].ReadEncodedBytes();
        Assert(first.SequenceEqual(expected), "Disk lazy reader changed original bytes.");
        first[0] = 0;
        Assert(result.Images[0].ReadEncodedBytes().SequenceEqual(expected), "Lazy reader must not retain a mutable encoded cache.");
        GltfDocument memory = GltfDocument.Parse(Encode(json, binary));
        Assert(memory.Images[0].ReadEncodedBytes().SequenceEqual(expected), "Memory source changed original bytes.");
    }
    finally { File.Delete(path); }
}

static void RejectsChangedTextureSource()
{
    foreach (bool changeLength in new[] { false, true })
    {
        var (json, binary) = TexturedTriangle();
        string path = TemporaryModelPath();
        try
        {
            WriteSparseGlb(path, json, binary, (binary.Length + 3) & ~3);
            GltfDocument result = GltfDocument.Load(path);
            if (changeLength)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
                file.SetLength(file.Length + 4);
            }
            else File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(2));
            bool rejected = false;
            try { _ = result.Images[0].ReadEncodedBytes(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Changed source must require reimport before texture allocation.");
        }
        finally { File.Delete(path); }
    }
}

static void BoundsImageMetadataAllocation()
{
    var (json, binary) = TexturedTriangle();
    json["bufferViews"]![2]!["byteLength"] = GltfDocument.MaximumEncodedImageBytes;
    string path = TemporaryModelPath();
    try
    {
        WriteSparseGlb(path, json, binary, GltfDocument.MaximumEncodedImageBytes + 44);
        long before = GC.GetAllocatedBytesForCurrentThread();
        GltfDocument result = GltfDocument.Load(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(result.Images[0].EncodedByteLength == GltfDocument.MaximumEncodedImageBytes, "Exact 64 MiB encoded image boundary rejected.");
        Assert(allocated < 2 * 1024 * 1024, "Metadata parsing retained or copied a whole encoded texture.");
    }
    finally { File.Delete(path); }
}

static void BoundsEncodedTextureBytes()
{
    foreach (bool totalLimit in new[] { false, true })
    {
        var (json, binary) = TexturedTriangle();
        int imageLength = totalLimit ? GltfDocument.MaximumEncodedImageBytes : GltfDocument.MaximumEncodedImageBytes + 1;
        json["bufferViews"]![2]!["byteLength"] = imageLength;
        if (totalLimit)
        {
            var images = new JsonArray();
            for (int i = 0; i < 9; i++) images.Add(json["images"]![0]!.DeepClone());
            json["images"] = images;
        }
        string path = TemporaryModelPath();
        try
        {
            WriteSparseGlb(path, json, binary, (imageLength + 44 + 3) & ~3);
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool rejected = false;
            try { _ = GltfDocument.Load(path); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected && GC.GetAllocatedBytesForCurrentThread() - before < 2 * 1024 * 1024,
                "Encoded image limit must reject before reading large image data.");
        }
        finally { File.Delete(path); }
    }
}

static void EstimatesTextureMemory()
{
    var image = new GltfImage(new byte[] { 1, 2, 3 }, "image/png", 4096, 4096);
    long decoded = 4096L * 4096 * 8;
    Assert(ModelTextureMemory.DecodedBytes(image) == decoded, "Full-resolution 16-bit decode estimate incorrect.");
    long mipPixels = Enumerable.Range(0, 13).Sum(level => (long)(4096 >> level) * (4096 >> level));
    long resident = decoded + mipPixels * 8 + image.EncodedByteLength;
    Assert(ModelTextureMemory.TextureResidentBytes(image) == resident, "Original-size GPU mip chain not accounted for.");
    Assert(ModelTextureMemory.Estimate(new[] { image }) == resident + decoded + 6 + ModelTextureMemory.GeometryReserveBytes,
        "Decode scratch, encoded copies or geometry headroom missing.");
    var narrow = new GltfImage(new byte[] { 1 }, "image/png", 1, 8192);
    Assert(ModelTextureMemory.TextureResidentBytes(narrow) == 8192 * 8L + 16383 * 8L + narrow.EncodedByteLength,
        "Narrow-texture mip chain must not rely on an unsafe 4/3 estimate.");
    Assert(ModelTextureMemory.Estimate(Array.Empty<GltfImage>()) == ModelTextureMemory.GeometryReserveBytes,
        "Geometry headroom missing for a textureless scene.");
}

static void EstimatesReferencedTextures()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 4096);
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 20, 4), 4096);
    var images = new JsonArray();
    for (int i = 0; i < 64; i++) images.Add(json["images"]![0]!.DeepClone());
    json["images"] = images;
    var result = GltfDocument.Parse(Encode(json, binary));
    int imageIndex = result.Textures[result.Materials[0].Texture].Image;
    long selected = ModelTextureMemory.Estimate(new[] { result.Images[imageIndex] });
    Assert(selected == ModelTextureMemory.Estimate(new[] { result.Images[0] }), "Unreferenced thumbnails entered selected-image estimate.");
    Assert(ModelTextureMemory.Estimate(result.Images) > selected * 32, "Texture memory must scale with actual selected images.");
}

static void EstimatesTexturePrecision()
{
    var (json, binary) = TexturedTriangle();
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 16, 4), 4096);
    BinaryPrimitives.WriteUInt32BigEndian(binary.AsSpan(44 + 20, 4), 4096);
    GltfImage ordinary = GltfDocument.Parse(Encode(json, binary)).Images[0];
    Assert(ordinary.EstimatedDecodedBytesPerPixel == 4 && ModelTextureMemory.DecodedBytes(ordinary) == 64L * 1024 * 1024,
        "Ordinary 4K textures must not be charged as 16-bit decoded textures.");
    binary[44 + 24] = 16;
    GltfImage deep = GltfDocument.Parse(Encode(json, binary)).Images[0];
    Assert(deep.EstimatedDecodedBytesPerPixel == 8 && ModelTextureMemory.DecodedBytes(deep) == 128L * 1024 * 1024,
        "High-depth 4K textures need twice the full-resolution decode budget.");
    Assert(ModelTextureMemory.TextureResidentBytes(deep) - deep.EncodedByteLength ==
        (ModelTextureMemory.TextureResidentBytes(ordinary) - ordinary.EncodedByteLength) * 2,
        "Precision-aware GPU mip accounting inconsistent.");
    binary[44 + 24] = 32;
    Reject(Encode(json, binary));

    var (jpegJson, original) = TexturedTriangle();
    // Header-only JPEG fixtures exercise SOF metadata, never native decoding.
    byte[] header = { 0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, 0x10, 0, 0x10, 0, 1, 1, 0x11, 0, 0xff, 0xd9 };
    byte[] jpegBinary = new byte[44 + header.Length];
    original.AsSpan(0, 44).CopyTo(jpegBinary); header.CopyTo(jpegBinary, 44);
    jpegJson["buffers"]![0]!["byteLength"] = jpegBinary.Length;
    jpegJson["bufferViews"]![2]!["byteLength"] = header.Length;
    jpegJson["images"]![0]!["mimeType"] = "image/jpeg";
    GltfImage jpeg = GltfDocument.Parse(Encode(jpegJson, jpegBinary)).Images[0];
    Assert(jpeg.Width == 4096 && jpeg.EstimatedDecodedBytesPerPixel == 4, "8-bit JPEG metadata estimate incorrect.");
    jpegBinary[44 + 6] = 12;
    Assert(GltfDocument.Parse(Encode(jpegJson, jpegBinary)).Images[0].EstimatedDecodedBytesPerPixel == 8,
        "Higher-precision JPEG needs a safe decode estimate.");
}

static void RejectsTextureMemoryInputs()
{
    foreach ((int width, int height) in new[] { (0, 4096), (-1, 4096), (8193, 1), (int.MaxValue, int.MaxValue) })
    {
        bool rejected = false;
        try { _ = new GltfImage(new byte[] { 1 }, "image/png", width, height); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Invalid dimensions must fail before estimator arithmetic.");
    }
    var image = new GltfImage(new byte[] { 1 }, "image/png", 1, 1);
    bool excessiveCount = false;
    try { _ = ModelTextureMemory.Estimate(Enumerable.Repeat(image, 65)); }
    catch (InvalidDataException) { excessiveCount = true; }
    Assert(excessiveCount, "Estimator must reject unbounded texture collections.");
    long maximal = ModelTextureMemory.Estimate(Enumerable.Repeat(new GltfImage(new byte[] { 1 }, "image/png", 8192, 8192), 64));
    Assert(maximal > 64L * 1024 * 1024 * 1024 && maximal < long.MaxValue, "Largest validated textures must not overflow memory arithmetic.");
}

static void EstimatesGeometryMemory()
{
    Assert(GltfDocument.MaximumVertices == 2_000_000 && GltfDocument.MaximumTriangles == 4_000_000,
        "Large-avatar vertex and triangle admission caps are not the supported 2M/4M boundaries.");
    // Metadata-sized numbers reflect a large avatar, without using its private
    // file or allocating any of its vertex, triangle or texture data in tests.
    const long vertices = 1_787_422, triangles = 3_031_584;
    long data = vertices * 56 + triangles * 12;
    var estimate = GltfGeometryMemory.Estimate(vertices, triangles, vertices, 100_000, 200_000);
    Assert(estimate.ManagedBytes == data + GltfGeometryMemory.MetadataReserveBytes &&
        estimate.NativeCpuBytes == data && estimate.GpuBytes == data,
        "CPU and GPU copies of original vertex/index/skin data were undercounted.");
    Assert(estimate.LargestTransientBytes == 100_000 * 64L + 200_000 * 24L + 1024 * 1024,
        "Largest primitive conversion scratch was omitted.");
    Assert(estimate.TotalBytes > 400L * 1024 * 1024 && estimate.TotalBytes < long.MaxValue,
        "Large-avatar estimate should be hundreds of MiB and must not overflow.");
    var all = GltfGeometryMemory.Estimate(GltfDocument.MaximumVertices, GltfDocument.MaximumTriangles,
        GltfDocument.MaximumVertices);
    Assert(all.TotalBytes > estimate.TotalBytes && all.TotalBytes < long.MaxValue,
        "Maximum supported geometry counts overflowed or silently used a zero-byte scratch allowance.");
    Assert(GltfGeometryMemory.Estimate(0, 0, 0).TotalBytes == GltfGeometryMemory.MetadataReserveBytes + 1024 * 1024,
        "Metadata and allocator reserves must remain even for empty geometry.");
}

static void BoundsGeometryMemory()
{
    var invalid = new (long Vertices, long Triangles, long Skin, int MaxVertices, int MaxTriangles)[]
    {
        (-1, 0, 0, 0, 0), (0, -1, 0, 0, 0), (1, 1, -1, 0, 0), (1, 1, 2, 0, 0),
        ((long)GltfDocument.MaximumVertices + 1, 1, 0, 0, 0),
        (1, (long)GltfDocument.MaximumTriangles + 1, 0, 0, 0),
        (long.MaxValue, long.MaxValue, long.MaxValue, 0, 0),
        (1, 1, 0, -1, 0), (1, 1, 0, 2, 0), (1, 1, 0, 0, -1), (1, 1, 0, 0, 2), (0, 1, 0, 0, 0)
    };
    foreach (var item in invalid)
    {
        bool rejected = false;
        try { _ = GltfGeometryMemory.Estimate(item.Vertices, item.Triangles, item.Skin, item.MaxVertices, item.MaxTriangles); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Invalid geometry memory inputs reached arithmetic or allocation.");
    }
    var (json, binary) = ManyJointTriangle(307);
    var primitive = GltfDocument.Parse(Encode(json, binary)).Nodes[0].Primitives[0];
    var skin = primitive.Skin!;
    long expected = primitive.Positions.Length * 4L + primitive.Normals!.Length * 4L + primitive.Indices.Length * 4L +
        skin.Weights.Length * 4L + skin.BoneIndices.Length * 2L + skin.Joints.Length * 128L;
    Assert(GltfGeometryMemory.NativeCpuBytesFor(primitive) == expected,
        "Native copy estimate omitted joint indices, weights or inverse bind matrices.");
    var palettes = GltfGeometryMemory.Estimate(3, 1, 3, skinJointReferences: 307 * 79);
    var noPalettes = GltfGeometryMemory.Estimate(3, 1, 3);
    Assert(palettes.ManagedBytes - noPalettes.ManagedBytes == 307 * 79 * 128L &&
        palettes.NativeCpuBytes - noPalettes.NativeCpuBytes == 307 * 79 * 128L &&
        palettes.GpuBytes - noPalettes.GpuBytes == 307 * 79 * 64L,
        "Repeated skeleton palette costs were omitted or counted only once.");
    foreach (long invalidReferences in new[] { -1L, GltfDocument.MaximumSkinJoints * 2048L + 1, long.MaxValue })
    {
        bool rejected = false;
        try { _ = GltfGeometryMemory.Estimate(3, 1, 3, skinJointReferences: invalidReferences); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Invalid skeleton reference counts reached arithmetic.");
    }
}

static void RunsGeometryPreflight()
{
    var (json, binary) = Triangle();
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(0, 4), BitConverter.SingleToInt32Bits(float.NaN));
    string path = TemporaryModelPath();
    File.WriteAllBytes(path, Encode(json, binary));
    try
    {
        bool called = false;
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = GltfDocument.Load(path, estimate =>
            {
                called = true;
                Assert(estimate.ManagedBytes > 0 && estimate.TotalBytes >= estimate.ManagedBytes,
                    "Preflight received an empty memory estimate.");
                throw new InvalidDataException("geometry-preflight-test-stop");
            });
            throw new InvalidOperationException("A rejected preflight must stop model loading.");
        }
        catch (InvalidDataException error)
        {
            Assert(error.Message == "geometry-preflight-test-stop", "Vertex bytes were read before the preflight callback.");
        }
        Assert(called, "Streamed model load did not invoke the geometry preflight callback.");
        Assert(GC.GetAllocatedBytesForCurrentThread() - allocationBefore < 2L * 1024 * 1024,
            "Small-model preflight allocated unbounded buffers.");
        BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(0, 4), BitConverter.SingleToInt32Bits(0));
        File.WriteAllBytes(path, Encode(json, binary));
        GltfGeometryMemory? received = null;
        var document = GltfDocument.Load(path, estimate => received = estimate);
        long actualNative = document.Nodes.SelectMany(node => node.Primitives)
            .Sum(primitive => GltfGeometryMemory.NativeCpuBytesFor(primitive));
        Assert(received is not null && document.GeometryMemory == received && received.NativeCpuBytes >= actualNative &&
            received.TotalBytes >= actualNative,
            "Geometry preflight undercounts native buffers or is not retained on the document.");
    }
    finally { File.Delete(path); }
}

static void PreservesPreflightRejection()
{
    var (json, binary) = Triangle();
    string path = TemporaryModelPath();
    File.WriteAllBytes(path, Encode(json, binary));
    try
    {
        const string message = "原画质模型内存不足；未修改贴图或减少面数。";
        bool preserved = false;
        try
        {
            _ = GltfDocument.Load(path, _ => throw new InvalidOperationException(message));
        }
        catch (InvalidOperationException error) { preserved = error.Message == message; }
        Assert(preserved, "Native allocator rejection was converted to a malformed-model JSON error.");
    }
    finally { File.Delete(path); }
}

static void CancelsGeometryPreflight()
{
    var (json, binary) = Triangle();
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(0, 4), BitConverter.SingleToInt32Bits(float.NaN));
    string path = TemporaryModelPath();
    File.WriteAllBytes(path, Encode(json, binary));
    try
    {
        using var beforeLoad = new CancellationTokenSource();
        beforeLoad.Cancel();
        bool callbackCalled = false, cancelled = false;
        try { _ = GltfDocument.Load(path, _ => callbackCalled = true, beforeLoad.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && !callbackCalled, "Pre-cancelled loading reached the allocator callback.");

        using var duringPreflight = new CancellationTokenSource();
        cancelled = false;
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = GltfDocument.Load(path, _ => duringPreflight.Cancel(), duringPreflight.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Preflight cancellation read invalid vertex bytes instead of stopping.");
        Assert(GC.GetAllocatedBytesForCurrentThread() - before < 2L * 1024 * 1024,
            "Cancelled preflight allocated unbounded geometry storage.");
    }
    finally { File.Delete(path); }
}

static void BoundsInverseBindPrefix()
{
    var (json, original) = SkinnedTriangle();
    byte[] binary = new byte[original.Length + 64];
    original.CopyTo(binary, 0);
    BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(original.Length, 4), BitConverter.SingleToInt32Bits(float.NaN));
    const int unusedMatrixCount = 1_000_000;
    json["bufferViews"]![4]!["byteLength"] = unusedMatrixCount * 64;
    json["accessors"]![4]!["count"] = unusedMatrixCount;
    string path = TemporaryModelPath();
    try
    {
        // A sparse 64 MB accessor gives a useful allocation regression without
        // keeping its unused data in RAM. NaN immediately after the single used
        // bind matrix also detects accidental full-accessor validation/reads.
        WriteSparseGlb(path, json, binary, 104 + unusedMatrixCount * 64);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var document = GltfDocument.Load(path);
        var skin = document.Nodes[0].Primitives[0].Skin!;
        Assert(skin.InverseBindMatrices.Length == 1 && skin.InverseBindMatrices[0] == Matrix4x4.CreateTranslation(0, -2, 0),
            "Used inverse bind prefix was lost, changed or expanded to the unused accessor tail.");
        Assert(GC.GetAllocatedBytesForCurrentThread() - before < 8L * 1024 * 1024,
            "Oversized inverse bind accessor caused a whole-tail allocation.");
    }
    finally { File.Delete(path); }
}

static string TemporaryModelPath() => Path.Combine(Path.GetTempPath(), "AliceVision-texture-test-" + Guid.NewGuid().ToString("N") + ".glb");

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

static (JsonObject Json, byte[] Binary) ManyJointTriangle(int jointCount)
{
    var (json, original) = Triangle();
    int matrixOffset = 116;
    byte[] binary = new byte[matrixOffset + jointCount * 64];
    original.CopyTo(binary, 0);
    json["buffers"]![0]!["byteLength"] = binary.Length;
    var views = json["bufferViews"]!.AsArray();
    views.Add(JsonNode.Parse("{\"buffer\":0,\"byteOffset\":44,\"byteLength\":24}"));
    views.Add(JsonNode.Parse("{\"buffer\":0,\"byteOffset\":68,\"byteLength\":48}"));
    views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = matrixOffset, ["byteLength"] = jointCount * 64 });
    var accessors = json["accessors"]!.AsArray();
    accessors.Add(JsonNode.Parse("{\"bufferView\":2,\"componentType\":5123,\"count\":3,\"type\":\"VEC4\"}"));
    accessors.Add(JsonNode.Parse("{\"bufferView\":3,\"componentType\":5126,\"count\":3,\"type\":\"VEC4\"}"));
    accessors.Add(new JsonObject { ["bufferView"] = 4, ["componentType"] = 5126, ["count"] = jointCount, ["type"] = "MAT4" });
    json["meshes"]![0]!["primitives"]![0]!["attributes"]!["JOINTS_0"] = 2;
    json["meshes"]![0]!["primitives"]![0]!["attributes"]!["WEIGHTS_0"] = 3;
    var nodes = new JsonArray { new JsonObject { ["mesh"] = 0, ["skin"] = 0 } };
    var joints = new JsonArray();
    var roots = new JsonArray { 0 };
    for (int i = 0; i < jointCount; i++)
    {
        nodes.Add(new JsonObject());
        joints.Add(i + 1);
        roots.Add(i + 1);
        Buffer.BlockCopy(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, 0,
            binary, matrixOffset + i * 64, 64);
    }
    json["nodes"] = nodes;
    json["skins"] = new JsonArray { new JsonObject { ["joints"] = joints, ["inverseBindMatrices"] = 4 } };
    json["scenes"]![0]!["nodes"] = roots;
    for (int i = 0; i < 3; i++)
    {
        int joint = i switch { 0 => 0, 1 => 256, _ => jointCount - 1 };
        BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(44 + i * 8, 2), (ushort)joint);
        Buffer.BlockCopy(new float[] { 1, 0, 0, 0 }, 0, binary, 68 + i * 16, 16);
    }
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
static string RejectionMessage(byte[] bytes)
{
    try { _ = GltfDocument.Parse(bytes); }
    catch (InvalidDataException error) { return error.Message; }
    throw new InvalidOperationException("Malformed model was not rejected safely.");
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
