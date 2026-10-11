using System.Globalization;
using System.Numerics;
using AliceVision.Core.Models;
using CoreGraphics;
using Foundation;
using ImageIO;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

/// <summary>
/// Displays validated embedded GLB / VRM geometry with native skinning.
/// Only app-authored humanoid poses run; imported clips, expressions, spring bones,
/// model shaders and scripts are never executed.
/// </summary>
internal static class CharacterGltfImporter
{
    internal const string RigRoot = "AliceVision.GLBRig";
    internal const string BonePrefix = "AliceVision.Humanoid.";
    public static SCNNode Load(string localPath) =>
        LoadAsync(localPath, CancellationToken.None, null, yieldBetweenSteps: false).GetAwaiter().GetResult();

    public static async Task<SCNNode> LoadAsync(string localPath, CancellationToken cancellation,
        Action<string>? progress = null, bool yieldBetweenSteps = true)
    {
        cancellation.ThrowIfCancellationRequested();
        ModelMemoryGuard.EnsureCanAllocate(128L * 1024 * 1024, "解析模型前检查");
        progress?.Invoke("正在解析模型；保留原始贴图分辨率…");
        GltfDocument ReadGuarded() => GltfDocument.Load(localPath,
            memory => ModelMemoryGuard.EnsureCanAllocate(memory.TotalBytes, "原网格内存预检查"), cancellation);
        GltfDocument document = yieldBetweenSteps
            ? await Task.Run(ReadGuarded, cancellation)
            : ReadGuarded();
        cancellation.ThrowIfCancellationRequested();
        var root = new SCNNode { Name = RigRoot };
        using var content = new SCNNode { Name = "AliceVision.AvatarCoordinates", Transform = ToSceneMatrix(document.AvatarBasis) };
        root.AddChildNode(content);
        var selectedNodes = new HashSet<int>();
        var pendingNodes = new Stack<int>(document.Roots);
        while (pendingNodes.TryPop(out int index))
        {
            if (!selectedNodes.Add(index)) continue;
            foreach (int child in document.Nodes[index].Children) pendingNodes.Push(child);
        }
        var selectedMaterials = selectedNodes.SelectMany(index => document.Nodes[index].Primitives)
            .Select(primitive => primitive.Material).Where(index => index >= 0).ToHashSet();
        var decodedImages = new UIImage?[document.Images.Count];
        var nativeMaterials = new SCNMaterial?[document.Materials.Count];
        var nodes = new SCNNode[document.Nodes.Count];
        try
        {
            var referencedImages = selectedMaterials.Select(index => document.Materials[index]).Where(material => material.Texture >= 0)
                .Select(material => document.Textures[material.Texture].Image).ToHashSet();
            var activeImages = referencedImages.Select(index => document.Images[index]).ToArray();
            // Managed geometry is now allocated, but native CPU/GPU copies are
            // still pending. Keep their full cost in the texture admission check.
            long remainingBudget = checked(ModelTextureMemory.Estimate(activeImages) +
                document.GeometryMemory.NativeCpuBytes + document.GeometryMemory.GpuBytes +
                document.GeometryMemory.LargestTransientBytes);
            ModelMemoryGuard.EnsureCanAllocate(remainingBudget, "原画质贴图与网格预检查");
            int loadedCount = 0;
            for (int imageIndex = 0; imageIndex < document.Images.Count; imageIndex++)
            {
                // VRM thumbnails and images for unsupported secondary material
                // properties are not uploaded when no displayed material uses them.
                if (!referencedImages.Contains(imageIndex)) continue;
                GltfImage image = document.Images[imageIndex];
                progress?.Invoke($"原画质贴图 {loadedCount + 1}/{activeImages.Length} · {image.Width} × {image.Height}");
                if (yieldBetweenSteps) await Task.Delay(1, cancellation);
                cancellation.ThrowIfCancellationRequested();
                ModelMemoryGuard.EnsureCanAllocate(remainingBudget, $"加载第 {loadedCount + 1} 张贴图前检查");
                // One encoded image at a time; the pool and ImageIO source/cache
                // are released before the next. No thumbnail, resize or recompress.
                using (var pool = new NSAutoreleasePool())
                    decodedImages[imageIndex] = DecodeOriginalImage(image);
                remainingBudget -= ModelTextureMemory.DecodedBytes(image);
                loadedCount++;
            }
            cancellation.ThrowIfCancellationRequested();
            ModelMemoryGuard.EnsureCanAllocate(remainingBudget, "构建原画质场景前检查");
            progress?.Invoke("正在绑定骨骼与原画质材质…");
            foreach (int index in selectedMaterials)
                nativeMaterials[index] = CreateMaterial(document.Materials[index], document, decodedImages);
            using var defaultMaterial = new SCNMaterial
            {
                LightingModelName = SCNLightingModel.PhysicallyBased,
                DoubleSided = false
            };
            defaultMaterial.Diffuse.Contents = UIColor.White;
            defaultMaterial.Metalness.Contents = new NSNumber(1.0f);
            defaultMaterial.Roughness.Contents = new NSNumber(1.0f);

            for (int i = 0; i < nodes.Length; i++)
            {
                GltfNode source = document.Nodes[i];
                nodes[i] = new SCNNode { Name = source.Name, Transform = ToSceneMatrix(source.Transform) };
            }
            foreach (var bone in document.HumanBones)
                nodes[bone.Value].Name = BonePrefix + bone.Key;
            for (int i = 0; i < nodes.Length; i++)
            {
                GltfNode source = document.Nodes[i];
                if (!selectedNodes.Contains(i)) continue;
                if (yieldBetweenSteps && i % 16 == 0) await Task.Delay(1, cancellation);
                cancellation.ThrowIfCancellationRequested();
                foreach (GltfPrimitive primitive in source.Primitives)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (yieldBetweenSteps) await Task.Delay(1, cancellation);
                    ModelMemoryGuard.EnsureCanAllocate(remainingBudget, "构建原网格与骨骼前检查");
                    using var pool = new NSAutoreleasePool();
                    using var geometry = CreateGeometry(primitive);
                    geometry.Materials = new[] { primitive.Material < 0 ? defaultMaterial : nativeMaterials[primitive.Material]! };
                    using var mesh = SCNNode.FromGeometry(geometry);
                    if (primitive.Skin is { } binding)
                    {
                        using var weights = CreateSource(binding.Weights, 4, SCNGeometrySourceSemantics.BoneWeights);
                        byte[] indexBytes = new byte[binding.BoneIndices.Length * sizeof(ushort)];
                        Buffer.BlockCopy(binding.BoneIndices, 0, indexBytes, 0, indexBytes.Length);
                        using var indexData = NSData.FromArray(indexBytes);
                        using var indices = SCNGeometrySource.FromData(indexData, SCNGeometrySourceSemantics.BoneIndices,
                            binding.BoneIndices.Length / 4, false, 4, sizeof(ushort), 0, 4 * sizeof(ushort));
                        using var skinner = SCNSkinner.Create(geometry, binding.Joints.Select(index => nodes[index]).ToArray(),
                            binding.InverseBindMatrices.Select(ToSceneMatrix).ToArray(), weights, indices);
                        skinner.BaseGeometryBindTransform = SCNMatrix4.Identity;
                        skinner.Skeleton = binding.Skeleton >= 0 ? nodes[binding.Skeleton] : content;
                        mesh.Skinner = skinner;
                        mesh.Name = "AliceVision.SkinnedMesh";
                        var minimum = new SCNVector3(binding.RestBoundsMinimum.X, binding.RestBoundsMinimum.Y, binding.RestBoundsMinimum.Z);
                        var maximum = new SCNVector3(binding.RestBoundsMaximum.X, binding.RestBoundsMaximum.Y, binding.RestBoundsMaximum.Z);
                        mesh.SetBoundingBox(ref minimum, ref maximum);
                        // glTF requires ignoring the skinned mesh node's world
                        // transform: jointWorld * inverseBind places its vertices.
                        // An identity child of the common content root implements
                        // that rule while retaining the original joint hierarchy.
                        content.AddChildNode(mesh);
                    }
                    else nodes[i].AddChildNode(mesh);
                    remainingBudget -= GltfGeometryMemory.NativeCpuBytesFor(primitive);
                }
            }
            for (int i = 0; i < nodes.Length; i++)
                foreach (int child in document.Nodes[i].Children) nodes[i].AddChildNode(nodes[child]);
            foreach (int index in document.Roots) content.AddChildNode(nodes[index]);
            cancellation.ThrowIfCancellationRequested();
            ModelMemoryGuard.EnsureCanAllocate(checked(ModelMemoryGuard.PendingTextureUploadBytes(activeImages) +
                document.GeometryMemory.GpuBytes + 32L * 1024 * 1024),
                "原画质 GPU 上传前检查");
            // SceneKit retains child nodes, geometries, materials and image contents.
            return root;
        }
        catch
        {
            CharacterAsset.DisposeNode(root);
            throw;
        }
        finally
        {
            foreach (var node in nodes) node?.Dispose();
            foreach (var material in nativeMaterials) material?.Dispose();
            foreach (var image in decodedImages) image?.Dispose();
        }
    }

    private static UIImage DecodeOriginalImage(GltfImage image)
    {
        using var data = NSData.FromArray(image.ReadEncodedBytes());
        using var source = CGImageSource.FromData(data, new CGImageOptions { ShouldCache = false })
            ?? throw new InvalidDataException("模型 PNG / JPEG 纹理无法读取。");
        var properties = source.GetProperties(0, new CGImageOptions { ShouldCache = false });
        if (source.ImageCount != 1 || properties is null || properties.PixelWidth != image.Width || properties.PixelHeight != image.Height)
            throw new InvalidDataException("模型纹理尺寸与原始文件不匹配，或包含多帧图像。");
        using var bitmap = source.CreateImage(0, new CGImageOptions { ShouldCache = true, ShouldCacheImmediately = true })
            ?? throw new InvalidDataException("模型 PNG / JPEG 纹理无法解码。");
        if (bitmap.Width != image.Width || bitmap.Height != image.Height ||
            bitmap.BitsPerPixel > image.EstimatedDecodedBytesPerPixel * 8)
            throw new InvalidDataException("模型原画质纹理尺寸或像素格式不受支持。");
        return UIImage.FromImage(bitmap);
    }

    private static SCNGeometry CreateGeometry(GltfPrimitive primitive)
    {
        var sources = new List<SCNGeometrySource>();
        try
        {
            sources.Add(CreateSource(primitive.Positions, 3, SCNGeometrySourceSemantics.Vertex));
            if (primitive.Normals is not null)
                sources.Add(CreateSource(primitive.Normals, 3, SCNGeometrySourceSemantics.Normal));
            if (primitive.Texcoords is not null)
            {
                // glTF image UVs start at the top left; SceneKit uses the bottom left.
                float[] coords = (float[])primitive.Texcoords.Clone();
                for (int i = 1; i < coords.Length; i += 2) coords[i] = 1 - coords[i];
                sources.Add(CreateSource(coords, 2, SCNGeometrySourceSemantics.Texcoord));
            }
            byte[] indices = new byte[primitive.Indices.Length * sizeof(uint)];
            Buffer.BlockCopy(primitive.Indices, 0, indices, 0, indices.Length);
            using var data = NSData.FromArray(indices);
            using var element = SCNGeometryElement.FromData(data, SCNGeometryPrimitiveType.Triangles, primitive.Indices.Length / 3, sizeof(uint));
            return SCNGeometry.Create(sources.ToArray(), new[] { element });
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }

    private static SCNGeometrySource CreateSource(float[] values, int components, SCNGeometrySourceSemantics semantic)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        using var data = NSData.FromArray(bytes);
        return SCNGeometrySource.FromData(data, semantic, values.Length / components, true,
            components, sizeof(float), 0, components * sizeof(float));
    }

    private static SCNMaterial CreateMaterial(GltfMaterial source, GltfDocument document, IReadOnlyList<UIImage?> images)
    {
        var material = new SCNMaterial
        {
            LightingModelName = source.Unlit ? SCNLightingModel.Constant : SCNLightingModel.PhysicallyBased,
            DoubleSided = source.DoubleSided,
            ReadsFromDepthBuffer = true,
            WritesToDepthBuffer = source.AlphaMode != "BLEND",
            TransparencyMode = SCNTransparencyMode.AOne,
            BlendMode = source.AlphaMode == "BLEND" ? SCNBlendMode.Alpha : SCNBlendMode.Replace
        };
        material.Metalness.Contents = new NSNumber(source.Metallic);
        material.Roughness.Contents = new NSNumber(source.Roughness);
        var color = UIColor.FromRGBA(source.BaseColor.X, source.BaseColor.Y, source.BaseColor.Z, 1.0f);
        if (source.Texture >= 0)
        {
            GltfTexture texture = document.Textures[source.Texture];
            material.Diffuse.Contents = images[texture.Image] ?? throw new InvalidDataException("模型纹理未加载。");
            material.Multiply.Contents = color;
            material.Diffuse.WrapS = Wrap(texture.WrapS);
            material.Diffuse.WrapT = Wrap(texture.WrapT);
            material.Diffuse.MinificationFilter = SCNFilterMode.Linear;
            material.Diffuse.MagnificationFilter = SCNFilterMode.Linear;
            material.Diffuse.MipFilter = SCNFilterMode.Linear;
        }
        else material.Diffuse.Contents = color;
        material.Transparency = source.AlphaMode == "OPAQUE" ? 1.0f : source.BaseColor.W;
        // These fixed app-authored shader snippets only implement the glTF alpha mode.
        // No shader strings from the model are accepted or executed.
        if (source.AlphaMode is "MASK" or "OPAQUE")
        {
            string fragment = source.AlphaMode == "MASK"
                ? "#pragma body\nif (_output.color.a < " + source.AlphaCutoff.ToString("0.000000", CultureInfo.InvariantCulture) +
                  ") { discard_fragment(); }\n_output.color.a = 1.0;"
                : "#pragma body\n_output.color.a = 1.0;";
            material.ShaderModifiers = new SCNShaderModifiers { EntryPointFragment = fragment };
        }
        return material;
    }

    private static SCNWrapMode Wrap(int wrap) => wrap switch
    {
        33071 => SCNWrapMode.Clamp, 33648 => SCNWrapMode.Mirror, _ => SCNWrapMode.Repeat
    };

    // System.Numerics uses row vectors; the fields here are the transposed glTF
    // column-major matrix, exactly the layout SceneKit's SCNMatrix4 binding uses.
    private static SCNMatrix4 ToSceneMatrix(Matrix4x4 matrix) => new(
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44);
}
