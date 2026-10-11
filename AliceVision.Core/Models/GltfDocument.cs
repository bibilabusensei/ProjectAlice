using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace AliceVision.Core.Models;

public sealed record GltfSkinBinding(int[] Joints, Matrix4x4[] InverseBindMatrices, float[] Weights,
    ushort[] BoneIndices, int Skeleton, Vector3 RestBoundsMinimum, Vector3 RestBoundsMaximum);
public sealed record GltfPrimitive(float[] Positions, float[]? Normals, float[]? Texcoords, uint[] Indices, int Material,
    GltfSkinBinding? Skin = null);
public sealed record GltfNode(string Name, Matrix4x4 Transform, int[] Children, IReadOnlyList<GltfPrimitive> Primitives);
public sealed record GltfTexture(int Image, int WrapS, int WrapT);
public sealed record GltfMaterial(Vector4 BaseColor, int Texture, float Metallic, float Roughness,
    bool DoubleSided, string AlphaMode, float AlphaCutoff, bool Unlit);

/// <summary>
/// Conservative, self-contained GLB 2.0 reader. It never resolves URIs or runs code.
/// Preserves validated skin influences and VRM humanoid mappings for native posing.
/// Authored animation clips, morphs and spring physics are not imported.
/// </summary>
public sealed class GltfDocument
{
    public const int MaximumFileBytes = 1 << 30;
    public const int MaximumVertices = 300_000;
    public const int MaximumTriangles = 500_000;
    public const int MaximumNodes = 4096;
    public const int MaximumImages = 64;
    public const int MaximumImageEdge = 8192;
    public const long MaximumImagePixels = 64L * 1024 * 1024;
    public const int MaximumEncodedImageBytes = 64 * 1024 * 1024;
    public const long MaximumTotalEncodedImageBytes = 512L * 1024 * 1024;
    private const int MaximumImageHeaderBytes = 1024 * 1024;
    private GltfDocument(GltfNode[] nodes, int[] roots, GltfImage[] images, GltfTexture[] textures,
        GltfMaterial[] materials, bool hasSkin, bool hasMorphs, bool hasAnimations,
        IReadOnlyDictionary<string, int> humanBones, bool legacyVrm)
    {
        Nodes = nodes; Roots = roots; Images = images; Textures = textures; Materials = materials;
        HasSkin = hasSkin; HasMorphs = hasMorphs; HasAnimations = hasAnimations;
        HumanBones = humanBones;
        IsLegacyVrm = legacyVrm;
    }
    public IReadOnlyList<GltfNode> Nodes { get; }
    public IReadOnlyList<int> Roots { get; }
    public IReadOnlyList<GltfImage> Images { get; }
    public IReadOnlyList<GltfTexture> Textures { get; }
    public IReadOnlyList<GltfMaterial> Materials { get; }
    public bool HasSkin { get; }
    public bool HasMorphs { get; }
    public bool HasAnimations { get; }
    public IReadOnlyDictionary<string, int> HumanBones { get; }
    public bool IsLegacyVrm { get; }
    // VRM0 is -Z forward; VRM1 is +Z forward. Raw mesh/skin data stay untouched.
    public Matrix4x4 AvatarBasis => IsLegacyVrm ? Matrix4x4.CreateRotationY(MathF.PI) : Matrix4x4.Identity;

    public static GltfDocument Load(string path)
    {
        return GltfFileSource.Read(path, ReadDocument);
    }

    public static GltfDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 28 or > MaximumFileBytes) throw Invalid("GLB 文件大小无效。");
        var span = bytes.Span;
        if (ReadU32(span, 0) != 0x46546c67 || ReadU32(span, 4) != 2 || ReadU32(span, 8) != bytes.Length)
            throw Invalid("需要完整的 GLB 2.0 / VRM 二进制模型。");
        int offset = 12;
        ReadOnlyMemory<byte> json = default, binary = default;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8) throw Invalid("GLB 块头不完整。");
            uint length = ReadU32(span, offset), type = ReadU32(span, offset + 4);
            offset += 8;
            if (length == 0 || length % 4 != 0 || length > bytes.Length - offset)
                throw Invalid("GLB 块长度或对齐无效。");
            if (type == 0x4e4f534a && json.IsEmpty && binary.IsEmpty && offset == 20)
            {
                if (length > 8 * 1024 * 1024) throw Invalid("模型 JSON 过大。");
                json = bytes.Slice(offset, (int)length);
            }
            else if (type == 0x004e4942 && !json.IsEmpty && binary.IsEmpty)
            {
                binary = bytes.Slice(offset, (int)length);
            }
            else throw Invalid("仅支持一个 JSON 块和一个嵌入 BIN 块。");
            offset += (int)length;
        }
        if (json.IsEmpty || binary.IsEmpty) throw Invalid("模型缺少嵌入几何数据。");
        return ReadDocument(json, new GltfMemorySource(binary));
    }

    private static GltfDocument ReadDocument(ReadOnlyMemory<byte> json, GltfBinarySource binary)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            return new Reader(document.RootElement, binary).Read();
        }
        catch (JsonException) { throw Invalid("模型 JSON 损坏或嵌套过深。"); }
        catch (InvalidOperationException) { throw Invalid("模型 JSON 字段类型无效。"); }
        catch (OverflowException) { throw Invalid("模型数据尺寸溢出。"); }
        catch (KeyNotFoundException) { throw Invalid("模型缺少必要字段。"); }
    }

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static InvalidDataException Invalid(string message) => new(message);

    private sealed class Reader
    {
        private readonly JsonElement _root;
        private readonly GltfBinarySource _binary;
        private int _declaredBinaryLength;
        private View[] _views = [];
        private Accessor[] _accessors = [];
        private JsonElement[] _rawNodes = [], _rawMeshes = [], _rawSkins = [];
        private int[][] _children = [];
        private int[] _parents = [], _depths = [];
        private Matrix4x4[] _local = [], _world = [];
        private int _vertexTotal, _triangleTotal, _primitiveTotal;
        private bool _hasMorphs;
        private sealed record View(int Offset, int Length, int Stride);
        private sealed record Accessor(int View, int Offset, int Count, int Component, int Components, string Type, bool Normalized);
        private sealed record Skin(int[] Joints, Matrix4x4[] InverseBind, int Skeleton);

        public Reader(JsonElement root, GltfBinarySource binary) { _root = root; _binary = binary; }

        public GltfDocument Read()
        {
            if (_root.ValueKind != JsonValueKind.Object) throw Invalid("模型 JSON 根节点必须是对象。");
            int itemBudget = 250_000;
            ValidateJson(_root, ref itemBudget);
            if (Text(_root.GetProperty("asset"), "version") != "2.0") throw Invalid("仅支持 glTF 2.0。");
            foreach (var extension in Array(_root, "extensionsRequired", 32))
            {
                string name = extension.GetString() ?? "";
                if (name is not ("VRM" or "VRMC_vrm" or "VRMC_materials_mtoon" or "VRMC_springBone" or
                    "VRMC_node_constraint" or "KHR_materials_unlit" or "KHR_texture_transform"))
                    throw Invalid("模型需要尚未支持的压缩、材质或几何扩展。");
            }
            var buffers = Array(_root, "buffers", 1);
            if (buffers.Length != 1) throw Invalid("仅支持单个嵌入 BIN 缓冲区。");
            int declaredLength = Integer(buffers[0], "byteLength");
            if (declaredLength < 1 || declaredLength > _binary.Length || _binary.Length - declaredLength > 3)
                throw Invalid("嵌入缓冲区大小不匹配。");
            _declaredBinaryLength = declaredLength;
            ReadViewsAndAccessors();
            GltfImage[] images = ReadImages();
            GltfTexture[] textures = ReadTextures(images.Length);
            GltfMaterial[] materials = ReadMaterials(textures.Length);
            ReadHierarchy();
            _rawMeshes = Array(_root, "meshes", 512);
            _rawSkins = Array(_root, "skins", 128);
            var nodes = new GltfNode[_rawNodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                var primitives = new List<GltfPrimitive>();
                if (_rawNodes[i].TryGetProperty("mesh", out var meshValue))
                {
                    int meshIndex = Index(meshValue, _rawMeshes.Length);
                    Skin? skin = _rawNodes[i].TryGetProperty("skin", out var skinValue) ? ReadSkin(Index(skinValue, _rawSkins.Length)) : null;
                    foreach (var primitive in Array(_rawMeshes[meshIndex], "primitives", 128))
                        primitives.Add(ReadPrimitive(primitive, i, skin, materials.Length));
                }
                nodes[i] = new GltfNode(Text(_rawNodes[i], "name", "Model node"), _local[i], _children[i], primitives);
            }
            if (_vertexTotal == 0) throw Invalid("模型中没有可显示的三角形网格。");
            int[] roots = ReadRoots();
            if (roots.Length == 0) throw Invalid("模型场景没有可显示的根节点。");
            var pending = new Stack<int>(roots);
            bool hasVisibleMesh = false;
            var visibleNodes = new HashSet<int>();
            while (pending.TryPop(out int node))
            {
                visibleNodes.Add(node);
                hasVisibleMesh |= nodes[node].Primitives.Count > 0;
                foreach (int child in _children[node]) pending.Push(child);
            }
            if (!hasVisibleMesh) throw Invalid("选中的模型场景没有可显示网格。");
            foreach (int index in visibleNodes)
                foreach (var primitive in nodes[index].Primitives)
                    if (primitive.Skin is { } skin && (skin.Joints.Any(joint => !visibleNodes.Contains(joint)) ||
                        (skin.Skeleton >= 0 && !visibleNodes.Contains(skin.Skeleton))))
                        throw Invalid("蒙皮骨骼必须包含在当前场景层级内。");
            var humanBones = ReadHumanBones(visibleNodes);
            bool legacyVrm = _root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("VRM", out _);
            return new GltfDocument(nodes, roots, images, textures, materials, _rawSkins.Length > 0,
                _hasMorphs, Array(_root, "animations", 256).Length > 0, humanBones, legacyVrm);
        }

        private static void ValidateJson(JsonElement element, ref int remaining)
        {
            if (--remaining < 0) throw Invalid("模型 JSON 对象过多。");
            if (element.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in element.EnumerateObject())
                {
                    if (!seen.Add(item.Name)) throw Invalid("模型 JSON 包含重复字段。");
                    if (item.Name == "uri") throw Invalid("仅支持自包含模型，不能读取外部文件或网络 URI。");
                    if (item.Name is "KHR_draco_mesh_compression" or "EXT_meshopt_compression")
                        throw Invalid("暂不支持 Draco 或 meshopt 压缩模型。");
                    ValidateJson(item.Value, ref remaining);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) ValidateJson(child, ref remaining);
        }

        private void ReadViewsAndAccessors()
        {
            var views = Array(_root, "bufferViews", 8192);
            _views = new View[views.Length];
            for (int i = 0; i < views.Length; i++)
            {
                int buffer = Integer(views[i], "buffer"), offset = Integer(views[i], "byteOffset", 0);
                int length = Integer(views[i], "byteLength"), stride = Integer(views[i], "byteStride", 0);
                if (buffer != 0 || offset < 0 || length < 1 || (long)offset + length > _declaredBinaryLength ||
                    (stride != 0 && (stride is < 4 or > 252 || stride % 4 != 0)))
                    throw Invalid("模型缓冲区视图越界或步幅无效。");
                _views[i] = new View(offset, length, stride);
            }
            var accessors = Array(_root, "accessors", 8192);
            _accessors = new Accessor[accessors.Length];
            for (int i = 0; i < accessors.Length; i++)
            {
                var value = accessors[i];
                if (value.TryGetProperty("sparse", out _)) throw Invalid("暂不支持稀疏访问器，请导出完整网格数据。");
                int viewIndex = Index(value.GetProperty("bufferView"), _views.Length);
                int offset = Integer(value, "byteOffset", 0), count = Integer(value, "count");
                int component = Integer(value, "componentType");
                string type = Text(value, "type");
                int components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => 0 };
                int size = ComponentSize(component);
                bool normalized = Boolean(value, "normalized", false);
                var view = _views[viewIndex];
                int elementSize = checked(size * components);
                int stride = view.Stride == 0 ? elementSize : view.Stride;
                if (components == 0 || count is < 1 or > MaximumTriangles * 3 || offset < 0 ||
                    offset % size != 0 || (view.Offset + (long)offset) % size != 0 || stride < elementSize ||
                    stride % size != 0 || offset + (long)(count - 1) * stride + elementSize > view.Length ||
                    (normalized && component is not (5120 or 5121 or 5122 or 5123)) || (type == "MAT4" && component != 5126))
                    throw Invalid("模型访问器格式、对齐或范围无效。");
                _accessors[i] = new Accessor(viewIndex, offset, count, component, components, type, normalized);
            }
        }

        private void ReadHierarchy()
        {
            _rawNodes = Array(_root, "nodes", MaximumNodes);
            _children = new int[_rawNodes.Length][];
            _parents = Enumerable.Repeat(-1, _rawNodes.Length).ToArray();
            _depths = new int[_rawNodes.Length];
            _local = new Matrix4x4[_rawNodes.Length]; _world = new Matrix4x4[_rawNodes.Length];
            for (int i = 0; i < _rawNodes.Length; i++)
            {
                _children[i] = Array(_rawNodes[i], "children", MaximumNodes).Select(v => Index(v, _rawNodes.Length)).ToArray();
                foreach (int child in _children[i])
                {
                    if (child == i || _parents[child] != -1) throw Invalid("模型层级包含循环或重复父节点。");
                    _parents[child] = i;
                }
                _local[i] = ReadTransform(_rawNodes[i]);
            }
            byte[] state = new byte[_rawNodes.Length];
            for (int i = 0; i < _rawNodes.Length; i++) ComputeWorld(i, state, 0);
        }

        private void ComputeWorld(int node, byte[] state, int depth)
        {
            if (state[node] == 2) return;
            if (state[node] == 1 || depth > 128) throw Invalid("模型层级循环或过深。");
            state[node] = 1;
            int parent = _parents[node];
            if (parent >= 0) ComputeWorld(parent, state, depth + 1);
            _depths[node] = parent < 0 ? 0 : _depths[parent] + 1;
            if (_depths[node] > 128) throw Invalid("模型层级超过 128 层。");
            _world[node] = parent < 0 ? _local[node] : _local[node] * _world[parent];
            if (!Finite(_world[node])) throw Invalid("模型变换数值溢出。");
            state[node] = 2;
        }

        private int[] ReadRoots()
        {
            var scenes = Array(_root, "scenes", 64);
            if (scenes.Length == 0) return Enumerable.Range(0, _rawNodes.Length).Where(i => _parents[i] < 0).ToArray();
            int scene = _root.TryGetProperty("scene", out var selected) ? Index(selected, scenes.Length) : 0;
            var roots = Array(scenes[scene], "nodes", MaximumNodes).Select(v => Index(v, _rawNodes.Length)).ToArray();
            if (roots.Distinct().Count() != roots.Length || roots.Any(i => _parents[i] >= 0))
                throw Invalid("模型场景根节点无效。");
            return roots;
        }

        private static Matrix4x4 ReadTransform(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var matrix))
            {
                if (node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _))
                    throw Invalid("模型节点不能同时使用矩阵和 TRS。");
                return Matrix(ReadFloats(matrix, 16));
            }
            float[] translation = node.TryGetProperty("translation", out var t) ? ReadFloats(t, 3) : [0, 0, 0];
            float[] rotation = node.TryGetProperty("rotation", out var r) ? ReadFloats(r, 4) : [0, 0, 0, 1];
            float[] scale = node.TryGetProperty("scale", out var s) ? ReadFloats(s, 3) : [1, 1, 1];
            var quaternion = new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]);
            if (MathF.Abs(quaternion.LengthSquared() - 1) > 0.01f) throw Invalid("模型四元数无效。");
            var result = Matrix4x4.CreateScale(scale[0], scale[1], scale[2]) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(quaternion)) *
                Matrix4x4.CreateTranslation(translation[0], translation[1], translation[2]);
            if (!Finite(result)) throw Invalid("模型节点变换无效。");
            return result;
        }

        private Skin ReadSkin(int index)
        {
            var skin = _rawSkins[index];
            int[] joints = Array(skin, "joints", 256).Select(v => Index(v, _rawNodes.Length)).ToArray();
            if (joints.Length == 0 || joints.Distinct().Count() != joints.Length) throw Invalid("模型骨骼列表无效。");
            int skeletonIndex = skin.TryGetProperty("skeleton", out var skeleton) ? Index(skeleton, _rawNodes.Length) : -1;
            Matrix4x4[] inverse = Enumerable.Repeat(Matrix4x4.Identity, joints.Length).ToArray();
            if (skin.TryGetProperty("inverseBindMatrices", out var accessorValue))
            {
                int accessor = Index(accessorValue, _accessors.Length);
                if (_accessors[accessor].Type != "MAT4" || _accessors[accessor].Component != 5126 || _accessors[accessor].Count < joints.Length)
                    throw Invalid("模型骨骼绑定矩阵无效。");
                float[] values = ReadValues(accessor, 16);
                for (int i = 0; i < joints.Length; i++) inverse[i] = Matrix(values.AsSpan(i * 16, 16));
            }
            return new Skin(joints, inverse, skeletonIndex);
        }

        private GltfPrimitive ReadPrimitive(JsonElement primitive, int nodeIndex, Skin? skin, int materialCount)
        {
            if (++_primitiveTotal > 2048) throw Invalid("模型网格部件超过 2048。");
            if (Integer(primitive, "mode", 4) != 4) throw Invalid("仅支持三角形网格。");
            var attributes = primitive.GetProperty("attributes");
            int positions = Index(attributes.GetProperty("POSITION"), _accessors.Length);
            var positionAccessor = _accessors[positions];
            if (positionAccessor.Component != 5126 || positionAccessor.Type != "VEC3" || positionAccessor.Count > MaximumVertices)
                throw Invalid("模型顶点必须是有限 Float VEC3。");
            float[] vertices = ReadValues(positions, 3);
            int vertexCount = vertices.Length / 3;
            _vertexTotal = checked(_vertexTotal + vertexCount);
            if (_vertexTotal > MaximumVertices) throw Invalid("模型顶点总数超过 300000。");
            float[]? normals = ReadAttribute(attributes, "NORMAL", 3, vertexCount, true);
            float[]? texcoords = ReadAttribute(attributes, "TEXCOORD_0", 2, vertexCount, false);
            uint[] indices;
            if (primitive.TryGetProperty("indices", out var indicesValue))
            {
                int id = Index(indicesValue, _accessors.Length);
                var accessor = _accessors[id];
                if (accessor.Type != "SCALAR" || accessor.Normalized || accessor.Component is not (5121 or 5123 or 5125) || accessor.Count % 3 != 0)
                    throw Invalid("模型三角形索引格式无效。");
                indices = new uint[accessor.Count];
                for (int i = 0; i < indices.Length; i++)
                {
                    indices[i] = ReadUnsigned(accessor, i, 0);
                    if (indices[i] >= vertexCount) throw Invalid("模型三角形索引越界。");
                }
            }
            else
            {
                if (vertexCount % 3 != 0) throw Invalid("无索引网格的顶点数必须是三的倍数。");
                indices = Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();
            }
            _triangleTotal = checked(_triangleTotal + indices.Length / 3);
            if (_triangleTotal > MaximumTriangles) throw Invalid("模型三角形总数超过 500000。");
            // Static preview fallback: shared vertices receive smooth normals when
            // the asset omits NORMAL. This avoids undefined PBR lighting; author
            // supplied normals are preserved verbatim for exact hard edges.
            normals ??= GeneratePreviewNormals(vertices, indices);
            if (primitive.TryGetProperty("targets", out var morphs))
            {
                if (morphs.ValueKind != JsonValueKind.Array || morphs.GetArrayLength() > 256) throw Invalid("模型形态目标无效。");
                _hasMorphs |= morphs.GetArrayLength() > 0;
            }
            GltfSkinBinding? binding = skin is not null ? ReadSkinBinding(attributes, vertices, normals, skin, nodeIndex) : null;
            int material = primitive.TryGetProperty("material", out var materialValue) ? Index(materialValue, materialCount) : -1;
            return new GltfPrimitive(vertices, normals, texcoords, indices, material, binding);
        }

        private static float[] GeneratePreviewNormals(float[] positions, uint[] indices)
        {
            var sums = new Vector3[positions.Length / 3];
            Vector3 Position(uint index)
            {
                int start = checked((int)index * 3);
                return new Vector3(positions[start], positions[start + 1], positions[start + 2]);
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = Position(indices[i]);
                Vector3 normal = Vector3.Cross(Position(indices[i + 1]) - a, Position(indices[i + 2]) - a);
                if (!Finite(normal)) throw Invalid("模型坐标过大，无法计算法线。");
                sums[indices[i]] += normal; sums[indices[i + 1]] += normal; sums[indices[i + 2]] += normal;
            }
            float[] result = new float[positions.Length];
            for (int i = 0; i < sums.Length; i++)
            {
                Vector3 normal = sums[i];
                if (!Finite(normal)) throw Invalid("模型法线累加溢出。");
                float scale = Math.Max(Math.Abs(normal.X), Math.Max(Math.Abs(normal.Y), Math.Abs(normal.Z)));
                normal = scale < 1e-20f ? Vector3.UnitY : Vector3.Normalize(normal / scale);
                result[i * 3] = normal.X; result[i * 3 + 1] = normal.Y; result[i * 3 + 2] = normal.Z;
            }
            return result;
        }

        private float[]? ReadAttribute(JsonElement attributes, string name, int components, int vertexCount, bool requireFloat)
        {
            if (!attributes.TryGetProperty(name, out var value)) return null;
            int index = Index(value, _accessors.Length);
            var accessor = _accessors[index];
            if (accessor.Count != vertexCount || accessor.Components != components ||
                (requireFloat && accessor.Component != 5126) ||
                (!requireFloat && accessor.Component != 5126 && !(accessor.Normalized && accessor.Component is 5121 or 5123)))
                throw Invalid("模型法线或纹理坐标格式无效。");
            return ReadValues(index, components);
        }

        private GltfSkinBinding ReadSkinBinding(JsonElement attributes, float[] vertices, float[]? normals, Skin skin, int nodeIndex)
        {
            if (!attributes.TryGetProperty("JOINTS_0", out var jointsValue) || !attributes.TryGetProperty("WEIGHTS_0", out var weightsValue) ||
                attributes.TryGetProperty("JOINTS_1", out _) || attributes.TryGetProperty("WEIGHTS_1", out _))
                throw Invalid("模型需要暂不支持的骨骼影响格式。");
            var joints = _accessors[Index(jointsValue, _accessors.Length)];
            int weightsIndex = Index(weightsValue, _accessors.Length);
            var weightsAccessor = _accessors[weightsIndex];
            int vertexCount = vertices.Length / 3;
            if (joints.Type != "VEC4" || joints.Component is not (5121 or 5123) || joints.Normalized || joints.Count != vertexCount ||
                weightsAccessor.Type != "VEC4" || weightsAccessor.Count != vertexCount ||
                (weightsAccessor.Component != 5126 && !(weightsAccessor.Normalized && weightsAccessor.Component is 5121 or 5123)) ||
                !Matrix4x4.Invert(_world[nodeIndex], out var inverseMesh)) throw Invalid("模型骨骼权重或网格变换无效。");
            float[] weights = ReadValues(weightsIndex, 4);
            ushort[] boneIndices = new ushort[checked(vertexCount * 4)];
            var boundsMinimum = new Vector3(float.PositiveInfinity);
            var boundsMaximum = new Vector3(float.NegativeInfinity);
            Matrix4x4[] matrices = new Matrix4x4[skin.Joints.Length];
            for (int i = 0; i < matrices.Length; i++) matrices[i] = skin.InverseBind[i] * _world[skin.Joints[i]] * inverseMesh;
            for (int i = 0; i < vertexCount; i++)
            {
                float total = 0;
                Matrix4x4 blend = default;
                for (int j = 0; j < 4; j++)
                {
                    float weight = weights[i * 4 + j];
                    uint joint = ReadUnsigned(joints, i, j);
                    if (weight < 0 || joint >= matrices.Length) throw Invalid("模型骨骼索引或权重越界。");
                    boneIndices[i * 4 + j] = checked((ushort)joint);
                    total += weight;
                    blend += matrices[joint] * weight;
                }
                if (!float.IsFinite(total) || total < 0.001f || MathF.Abs(total - 1) > 0.05f) throw Invalid("模型骨骼权重总和无效。");
                for (int j = 0; j < 4; j++) weights[i * 4 + j] /= total;
                blend *= 1 / total;
                var position = Vector3.Transform(new Vector3(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]), blend);
                if (!Finite(position)) throw Invalid("模型蒙皮坐标溢出。");
                var worldPosition = Vector3.Transform(position, _world[nodeIndex]);
                if (!Finite(worldPosition)) throw Invalid("模型蒙皮全局坐标溢出。");
                boundsMinimum = Vector3.Min(boundsMinimum, worldPosition);
                boundsMaximum = Vector3.Max(boundsMaximum, worldPosition);
                if (normals is not null)
                {
                    if (!Matrix4x4.Invert(blend, out var inverse)) throw Invalid("模型法线变换不可逆。");
                    var normal = Vector3.TransformNormal(new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]), Matrix4x4.Transpose(inverse));
                    if (!Finite(normal) || normal.LengthSquared() < 0.000001f) throw Invalid("模型蒙皮法线无效。");
                }
            }
            // Keep the source bind geometry unmodified. Baking these coordinates
            // would apply skinning a second time when SCNSkinner evaluates bones.
            return new GltfSkinBinding(skin.Joints, skin.InverseBind, weights, boneIndices, skin.Skeleton, boundsMinimum, boundsMaximum);
        }

        private IReadOnlyDictionary<string, int> ReadHumanBones(HashSet<int> visibleNodes)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var assignedNodes = new HashSet<int>();
            if (!_root.TryGetProperty("extensions", out var extensions)) return result;
            bool legacy = extensions.TryGetProperty("VRM", out var vrm0);
            bool modern = extensions.TryGetProperty("VRMC_vrm", out var vrm1);
            if (legacy && modern) throw Invalid("模型不能同时声明两个版本的 VRM 人形骨骼。");
            if (legacy && vrm0.TryGetProperty("humanoid", out var humanoid0))
            {
                foreach (var bone in Array(humanoid0, "humanBones", 64))
                    Add(Text(bone, "bone"), Index(bone.GetProperty("node"), _rawNodes.Length));
            }
            if (modern && vrm1.TryGetProperty("humanoid", out var humanoid1) &&
                humanoid1.TryGetProperty("humanBones", out var bones))
            {
                if (bones.ValueKind != JsonValueKind.Object) throw Invalid("VRM 人形骨骼必须是对象。");
                int count = 0;
                foreach (var bone in bones.EnumerateObject())
                {
                    if (++count > 64) throw Invalid("VRM 人形骨骼数量超过预算。");
                    Add(bone.Name, Index(bone.Value.GetProperty("node"), _rawNodes.Length));
                }
            }
            return result;

            void Add(string name, int index)
            {
                if (!HumanBoneNames.Contains(name) || !visibleNodes.Contains(index) ||
                    !assignedNodes.Add(index) || !result.TryAdd(name, index))
                    throw Invalid("VRM 人形骨骼名称、场景索引或唯一性无效。");
            }
        }

        private static readonly HashSet<string> HumanBoneNames = new(StringComparer.Ordinal)
        {
            "hips", "spine", "chest", "upperChest", "neck", "head", "leftEye", "rightEye", "jaw",
            "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes", "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes",
            "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand", "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand",
            "leftThumbMetacarpal", "leftThumbProximal", "leftThumbIntermediate", "leftThumbDistal",
            "leftIndexProximal", "leftIndexIntermediate", "leftIndexDistal", "leftMiddleProximal", "leftMiddleIntermediate", "leftMiddleDistal",
            "leftRingProximal", "leftRingIntermediate", "leftRingDistal", "leftLittleProximal", "leftLittleIntermediate", "leftLittleDistal",
            "rightThumbMetacarpal", "rightThumbProximal", "rightThumbIntermediate", "rightThumbDistal",
            "rightIndexProximal", "rightIndexIntermediate", "rightIndexDistal", "rightMiddleProximal", "rightMiddleIntermediate", "rightMiddleDistal",
            "rightRingProximal", "rightRingIntermediate", "rightRingDistal", "rightLittleProximal", "rightLittleIntermediate", "rightLittleDistal"
        };

        private float[] ReadValues(int index, int components)
        {
            var accessor = _accessors[index];
            if (accessor.Components != components) throw Invalid("模型访问器分量数不匹配。");
            float[] values = new float[checked(accessor.Count * components)];
            for (int i = 0; i < accessor.Count; i++)
                for (int j = 0; j < components; j++)
                {
                    var bytes = ComponentBytes(accessor, i, j);
                    float value = accessor.Component switch
                    {
                        5126 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                        5120 => accessor.Normalized ? Math.Max((sbyte)bytes[0] / 127f, -1) : (sbyte)bytes[0],
                        5121 => accessor.Normalized ? bytes[0] / 255f : bytes[0],
                        5122 => accessor.Normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32767f, -1) : BinaryPrimitives.ReadInt16LittleEndian(bytes),
                        5123 => accessor.Normalized ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                        _ => throw Invalid("模型浮点属性分量类型不支持。")
                    };
                    if (!float.IsFinite(value)) throw Invalid("模型包含 NaN 或无穷坐标。");
                    values[i * components + j] = value;
                }
            return values;
        }

        private uint ReadUnsigned(Accessor accessor, int index, int component)
        {
            var bytes = ComponentBytes(accessor, index, component);
            return accessor.Component switch
            {
                5121 => bytes[0], 5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes), _ => throw Invalid("模型无符号索引格式不支持。")
            };
        }

        private ReadOnlySpan<byte> ComponentBytes(Accessor accessor, int index, int component)
        {
            var view = _views[accessor.View];
            int size = ComponentSize(accessor.Component);
            int stride = view.Stride == 0 ? size * accessor.Components : view.Stride;
            int offset = checked(view.Offset + accessor.Offset + index * stride + component * size);
            return _binary.Slice(offset, size);
        }

        private GltfImage[] ReadImages()
        {
            var images = Array(_root, "images", MaximumImages);
            var result = new GltfImage[images.Length];
            long encodedTotal = 0;
            for (int i = 0; i < images.Length; i++)
            {
                var view = _views[Index(images[i].GetProperty("bufferView"), _views.Length)];
                string mime = Text(images[i], "mimeType");
                if (view.Length > MaximumEncodedImageBytes || mime is not ("image/png" or "image/jpeg"))
                    throw Invalid("仅支持不超过 64 MB 的嵌入 PNG / JPEG 纹理。");
                encodedTotal = checked(encodedTotal + view.Length);
                if (encodedTotal > MaximumTotalEncodedImageBytes)
                    throw Invalid("模型嵌入贴图总量超过 512 MB 安全限制。");
                // Read only a bounded metadata prefix here. Actual full-resolution
                // decoding happens once per referenced image after device-memory
                // preflight; no raw total-pixel cap rejects unused thumbnails.
                int headerLength = Math.Min(view.Length, mime == "image/png" ? 33 : MaximumImageHeaderBytes);
                byte[] header = _binary.ReadBytes(view.Offset, headerLength);
                (int width, int height, int decodedBytesPerPixel) = ImageMetadata(header, mime);
                if (width <= 0 || height <= 0 || width > MaximumImageEdge || height > MaximumImageEdge ||
                    (long)width * height > MaximumImagePixels)
                    throw Invalid($"第 {i + 1} 张纹理尺寸 {width}×{height} 超过 8192×8192 安全限制；未降低画质。");
                result[i] = new GltfImage(view.Length, mime, width, height,
                    _binary.CaptureReader(view.Offset, view.Length), decodedBytesPerPixel);
            }
            return result;
        }

        private GltfTexture[] ReadTextures(int imageCount)
        {
            var textures = Array(_root, "textures", 64);
            var samplers = Array(_root, "samplers", 64);
            var result = new GltfTexture[textures.Length];
            for (int i = 0; i < textures.Length; i++)
            {
                int image = Index(textures[i].GetProperty("source"), imageCount), wrapS = 10497, wrapT = 10497;
                if (textures[i].TryGetProperty("sampler", out var samplerValue))
                {
                    var sampler = samplers[Index(samplerValue, samplers.Length)];
                    wrapS = Integer(sampler, "wrapS", 10497); wrapT = Integer(sampler, "wrapT", 10497);
                    if (wrapS is not (33071 or 33648 or 10497) || wrapT is not (33071 or 33648 or 10497))
                        throw Invalid("模型纹理重复模式无效。");
                }
                result[i] = new GltfTexture(image, wrapS, wrapT);
            }
            return result;
        }

        private GltfMaterial[] ReadMaterials(int textureCount)
        {
            var materials = Array(_root, "materials", 512);
            var result = new GltfMaterial[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                Vector4 color = Vector4.One; int texture = -1; float metallic = 1, roughness = 1;
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var factor))
                    {
                        float[] values = ReadFloats(factor, 4);
                        if (values.Any(value => value is < 0 or > 1)) throw Invalid("模型颜色分量必须在 0 到 1 之间。");
                        color = new Vector4(values[0], values[1], values[2], values[3]);
                    }
                    metallic = UnitFloat(pbr, "metallicFactor", 1); roughness = UnitFloat(pbr, "roughnessFactor", 1);
                    if (pbr.TryGetProperty("baseColorTexture", out var textureValue))
                    {
                        texture = Index(textureValue.GetProperty("index"), textureCount);
                        if (Integer(textureValue, "texCoord", 0) != 0)
                            throw Invalid("暂不支持非零纹理坐标集。");
                        ValidateIdentityTextureTransform(textureValue);
                    }
                }
                string alpha = Text(material, "alphaMode", "OPAQUE");
                if (alpha is not ("OPAQUE" or "MASK" or "BLEND")) throw Invalid("模型透明模式无效。");
                bool unlit = material.TryGetProperty("extensions", out var extensions) &&
                    (extensions.TryGetProperty("KHR_materials_unlit", out _) || extensions.TryGetProperty("VRMC_materials_mtoon", out _));
                result[i] = new GltfMaterial(color, texture, metallic, roughness, Boolean(material, "doubleSided", false),
                    alpha, UnitFloat(material, "alphaCutoff", 0.5f), unlit);
            }
            return result;
        }

        private static void ValidateIdentityTextureTransform(JsonElement texture)
        {
            if (!texture.TryGetProperty("extensions", out var extensions)) return;
            if (extensions.ValueKind != JsonValueKind.Object) throw Invalid("模型纹理扩展格式无效。");
            foreach (var extension in extensions.EnumerateObject())
            {
                if (extension.Name != "KHR_texture_transform") throw Invalid("暂不支持此纹理扩展。");
                var transform = extension.Value;
                float[] offset = transform.TryGetProperty("offset", out var offsetValue) ? ReadFloats(offsetValue, 2) : [0, 0];
                float[] scale = transform.TryGetProperty("scale", out var scaleValue) ? ReadFloats(scaleValue, 2) : [1, 1];
                float rotation = transform.TryGetProperty("rotation", out var angle) ? angle.GetSingle() : 0;
                if (!float.IsFinite(rotation) || Math.Abs(rotation) > 0.000001f ||
                    offset.Any(v => Math.Abs(v) > 0.000001f) || scale.Any(v => Math.Abs(v - 1) > 0.000001f) ||
                    Integer(transform, "texCoord", 0) != 0)
                    throw Invalid("暂不支持非默认纹理偏移、旋转、缩放或坐标集。");
            }
        }

        private static (int Width, int Height, int DecodedBytesPerPixel) ImageMetadata(byte[] bytes, string mime)
        {
            if (mime == "image/png")
            {
                ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
                if (bytes.Length < 33 || !bytes.AsSpan(0, 8).SequenceEqual(signature) ||
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 ||
                    !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw Invalid("模型 PNG 纹理头损坏。");
                int depth = bytes[24], color = bytes[25];
                bool validDepth = color switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false
                };
                if (!validDepth || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1)
                    throw Invalid("模型 PNG 位深、颜色类型或压缩头无效。");
                return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4))),
                    checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4))), depth == 16 ? 8 : 4);
            }
            if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) throw Invalid("模型 JPEG 纹理头损坏。");
            int offset = 2;
            while (offset + 4 <= bytes.Length)
            {
                if (bytes[offset++] != 255) throw Invalid("模型 JPEG 标记损坏。");
                while (offset < bytes.Length && bytes[offset] == 255) offset++;
                if (offset >= bytes.Length) break;
                byte marker = bytes[offset++];
                if (marker is 0xda or 0xd9) break;
                if (marker is >= 0xd0 and <= 0xd7 or 0x01) continue;
                if (offset + 2 > bytes.Length) break;
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                if (length < 2 || offset + length > bytes.Length) throw Invalid("模型 JPEG 段越界。");
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (length < 8 || bytes[offset + 2] is < 1 or > 16 || bytes[offset + 7] is < 1 or > 4)
                        throw Invalid("模型 JPEG 尺寸段损坏。");
                    return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)), bytes[offset + 2] > 8 ? 8 : 4);
                }
                offset += length;
            }
            throw Invalid("模型 JPEG 缺少有效尺寸。");
        }

        private static JsonElement[] Array(JsonElement owner, string key, int maximum)
        {
            if (!owner.TryGetProperty(key, out var value)) return [];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Invalid("模型数组大小或类型无效。");
            return value.EnumerateArray().ToArray();
        }
        private static int Index(JsonElement value, int count)
        {
            if (!value.TryGetInt32(out int index) || index < 0 || index >= count) throw Invalid("模型引用索引越界。");
            return index;
        }
        private static int Integer(JsonElement owner, string key, int? fallback = null)
        {
            if (!owner.TryGetProperty(key, out var value)) return fallback ?? throw Invalid("模型缺少必要整数字段。");
            if (!value.TryGetInt32(out int result)) throw Invalid("模型整数字段无效。");
            return result;
        }
        private static string Text(JsonElement owner, string key, string? fallback = null)
        {
            if (!owner.TryGetProperty(key, out var value)) return fallback ?? throw Invalid("模型缺少必要文字字段。");
            string result = value.GetString() ?? throw Invalid("模型文字字段无效。");
            if (result.Length > 256 || result.Any(char.IsControl)) throw Invalid("模型文字字段过长或无效。");
            return result;
        }
        private static bool Boolean(JsonElement owner, string key, bool fallback) =>
            owner.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
        private static float UnitFloat(JsonElement owner, string key, float fallback)
        {
            float result = owner.TryGetProperty(key, out var value) ? value.GetSingle() : fallback;
            if (!float.IsFinite(result) || result is < 0 or > 1) throw Invalid("模型材质数值无效。");
            return result;
        }
        private static float[] ReadFloats(JsonElement value, int count)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count) throw Invalid("模型向量分量数无效。");
            float[] values = value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (values.Any(v => !float.IsFinite(v))) throw Invalid("模型向量包含 NaN 或无穷数。");
            return values;
        }
        private static int ComponentSize(int component) => component switch
        {
            5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4,
            _ => throw Invalid("模型访问器分量类型不支持。")
        };
        private static Matrix4x4 Matrix(ReadOnlySpan<float> values)
        {
            var result = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
            if (!Finite(result) || MathF.Abs(result.M14) > 0.00001f || MathF.Abs(result.M24) > 0.00001f ||
                MathF.Abs(result.M34) > 0.00001f || MathF.Abs(result.M44 - 1) > 0.00001f) throw Invalid("模型矩阵不是有效仿射变换。");
            return result;
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        private static bool Finite(Matrix4x4 m) =>
            float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
            float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
            float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
            float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
    }
}
