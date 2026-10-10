using System.Globalization;
using System.Numerics;
using AliceVision.Core.Models;
using CoreGraphics;
using Foundation;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

/// <summary>
/// Displays validated embedded GLB / VRM geometry in a static initial pose.
/// No imported animations, expressions, spring bones, shaders or scripts are executed.
/// </summary>
internal static class CharacterGltfImporter
{
    public const bool SupportsWave = false;
    public const bool SupportsWalk = false;
    public const int MaximumPreviewTextureEdge = 1024;

    public static SCNNode Load(string localPath)
    {
        GltfDocument document = GltfDocument.Load(localPath);
        var root = new SCNNode { Name = "AliceVision.StaticGLBPreview" };
        var decodedImages = new UIImage?[document.Images.Count];
        var nativeMaterials = new List<SCNMaterial>();
        try
        {
            var referencedImages = document.Materials.Where(material => material.Texture >= 0)
                .Select(material => document.Textures[material.Texture].Image).ToHashSet();
            for (int imageIndex = 0; imageIndex < document.Images.Count; imageIndex++)
            {
                // VRM thumbnails and images for unsupported secondary material
                // properties are not uploaded when no displayed material uses them.
                if (!referencedImages.Contains(imageIndex)) continue;
                GltfImage image = document.Images[imageIndex];
                using var data = NSData.FromArray(image.Bytes);
                UIImage decoded = UIImage.LoadFromData(data) ?? throw new InvalidDataException("模型 PNG / JPEG 纹理无法解码。");
                try
                {
                    using var bitmap = decoded.CGImage;
                    if (bitmap is null || bitmap.Width != image.Width || bitmap.Height != image.Height)
                        throw new InvalidDataException("模型纹理尺寸与解码结果不匹配。");
                    UIImage preview = CreateTexturePreview(decoded, image.Width, image.Height);
                    if (!ReferenceEquals(preview, decoded))
                    {
                        decoded.Dispose();
                        decoded = preview;
                    }
                    decodedImages[imageIndex] = decoded;
                }
                catch
                {
                    decoded.Dispose();
                    throw;
                }
            }
            foreach (GltfMaterial source in document.Materials)
                nativeMaterials.Add(CreateMaterial(source, document, decodedImages));
            using var defaultMaterial = new SCNMaterial
            {
                LightingModelName = SCNLightingModel.PhysicallyBased,
                DoubleSided = false
            };
            defaultMaterial.Diffuse.Contents = UIColor.White;
            defaultMaterial.Metalness.Contents = new NSNumber(1.0f);
            defaultMaterial.Roughness.Contents = new NSNumber(1.0f);

            var nodes = new SCNNode[document.Nodes.Count];
            for (int i = 0; i < nodes.Length; i++)
            {
                GltfNode source = document.Nodes[i];
                var node = new SCNNode { Name = source.Name, Transform = ToSceneMatrix(source.Transform) };
                foreach (GltfPrimitive primitive in source.Primitives)
                {
                    using var geometry = CreateGeometry(primitive);
                    geometry.Materials = new[] { primitive.Material < 0 ? defaultMaterial : nativeMaterials[primitive.Material] };
                    node.AddChildNode(SCNNode.FromGeometry(geometry));
                }
                nodes[i] = node;
            }
            for (int i = 0; i < nodes.Length; i++)
                foreach (int child in document.Nodes[i].Children) nodes[i].AddChildNode(nodes[child]);
            foreach (int index in document.Roots) root.AddChildNode(nodes[index]);
            // SceneKit retains child nodes, geometries, materials and image contents.
            foreach (SCNNode node in nodes) node.Dispose();
            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
        finally
        {
            foreach (var material in nativeMaterials) material.Dispose();
            foreach (var image in decodedImages) image?.Dispose();
        }
    }

    private static UIImage CreateTexturePreview(UIImage source, int width, int height)
    {
        int longestEdge = Math.Max(width, height);
        if (longestEdge <= MaximumPreviewTextureEdge) return source;
        double scale = MaximumPreviewTextureEdge / (double)longestEdge;
        int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var format = new UIGraphicsImageRendererFormat
        {
            Scale = 1,
            Opaque = false,
            PreferredRange = UIGraphicsImageRendererFormatRange.Standard
        };
        using var renderer = new UIGraphicsImageRenderer(new CGSize(targetWidth, targetHeight), format);
        // Preserve the image aspect and alpha, and use physical pixels rather than
        // the device's 3x display scale. SceneKit only retains this smaller image.
        UIImage preview = renderer.CreateImage(_ => source.Draw(new CGRect(0, 0, targetWidth, targetHeight)));
        using var result = preview.CGImage;
        if (result is null || result.Width != targetWidth || result.Height != targetHeight)
        {
            preview.Dispose();
            throw new InvalidDataException("模型纹理缩小失败。");
        }
        return preview;
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
