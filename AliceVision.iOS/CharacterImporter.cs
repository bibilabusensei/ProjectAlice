using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Foundation;
using ModelIO;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>Single local-file model import; the picker first copies a selected file into the app.</summary>
public static class CharacterImporter
{
    public const long MaximumFileBytes = 1L << 30;
    public static IReadOnlyList<string> AllowedExtensions { get; } = new[] { ".usdz", ".dae", ".obj", ".glb", ".vrm" };

    public static CharacterAsset Load(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        string path = Path.GetFullPath(localPath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            throw new NotSupportedException("当前支持 USDZ、DAE、OBJ、二进制 GLB 和 VRM。请先从 Blender 导出；暂不直接读取 .blend 或 FBX。");
        }
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("模型文件为空、不存在或超过 1 GB（1024 MB）。");
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("请选择普通本地模型文件。");
        }
        switch (extension)
        {
            case ".usdz": ValidateUsdz(path); break;
            case ".dae": ValidateDae(path); break;
            case ".obj": ValidateObj(path); break;
        }

        if (extension is ".glb" or ".vrm")
        {
            var preview = CharacterGltfImporter.Load(path);
            try
            {
                _ = SanitizeAndCollect(preview, clearShaders: false);
                var actions = CharacterMotion.ActionsForImportedRig(preview);
                return new CharacterAsset(Path.GetFileNameWithoutExtension(path),
                    actions.Count > 1
                        ? "已保留 VRM 人形骨骼与蒙皮，可使用应用提供的招呼、挥手、点头、鞠躬、原地步态和舞动；不播放文件动画、表情或弹簧物理。"
                        : "GLB / VRM 网格和贴图预览；未找到完整可用的人形蒙皮绑定，暂只支持待机、转向与空间平移，不宣告骨骼动作。",
                    preview, Array.Empty<string>(), actions);
            }
            catch
            {
                preview.Dispose();
                throw;
            }
        }

        using var url = NSUrl.FromFilename(path);
        using var directoryUrl = NSUrl.FromFilename(file.DirectoryName!);
        SCNScene? scene;
        if (extension == ".obj")
        {
            if (!MDLAsset.CanImportFileExtension("obj"))
            {
                throw new NotSupportedException("此系统的 Model I/O 无法读取 OBJ，请改用 USDZ。");
            }
            using var asset = new MDLAsset(url);
            scene = SCNScene.FromAsset(asset);
        }
        else
        {
            // OverrideAssetUrls redirects resource lookup to this import directory.
            // useSafeMode is retained for older loaders but is not a modern security
            // sandbox; preflight rejects external DAE references and unsafe ZIP paths.
            var options = new SCNSceneLoadingOptions
            {
                AssetDirectoryUrls = new[] { directoryUrl },
                OverrideAssetUrls = true,
                UseSafeMode = true,
                CheckConsistency = true,
                CreateNormalsIfAbsent = true,
                ConvertToYUp = true,
                FlattenScene = false,
                AnimationImportPolicy = SCNAnimationImportPolicy.Play
            };
            scene = SCNScene.FromUrl(url, options, out var error);
            if (scene is null)
            {
                string message = error?.LocalizedDescription ?? "系统未能读取模型。";
                error?.Dispose();
                throw new InvalidDataException(message);
            }
            error?.Dispose();
        }
        if (scene is null)
        {
            throw new InvalidDataException("模型没有可读取的场景。建议使用包含贴图的 USDZ。");
        }

        using (scene)
        {
            var root = CharacterAsset.ClonePreservingRig(scene.RootNode);
            try
            {
                var clips = SanitizeAndCollect(root);
                var boundsMin = new SCNVector3();
                var boundsMax = new SCNVector3();
                if (!root.GetBoundingBox(ref boundsMin, ref boundsMax) ||
                    !float.IsFinite(boundsMax.Y - boundsMin.Y) || boundsMax.Y - boundsMin.Y < 0.00001f)
                {
                    throw new InvalidDataException("模型没有有效高度。请只导出角色并使用 Y 轴向上。");
                }
                string description = clips.Count == 0
                    ? "已导入模型；没有可用的内置动画。可放置、转向或移动，移动不会自动生成骨骼步态。"
                    : $"已导入模型，发现 {clips.Count} 个命名动画。请手动选择；动画可能自带位移，不参与移动路线验证。不会自动套用挥手或行走骨骼。";
                if (extension == ".obj")
                {
                    description += " 单文件 OBJ 不含独立贴图或骨骼动画；完整材质请改用 USDZ。";
                }
                return new CharacterAsset(Path.GetFileNameWithoutExtension(path), description, root, clips);
            }
            catch
            {
                root.Dispose();
                throw;
            }
        }
    }

    private static List<string> SanitizeAndCollect(SCNNode root, bool clearShaders = true)
    {
        var nodes = CharacterMotion.Enumerate(root).ToList();
        if (nodes.Count >= 4096)
        {
            throw new InvalidDataException("模型节点过多，请合并不必要的对象后重新导出。");
        }
        var clips = new HashSet<string>(StringComparer.Ordinal);
        long vertexTotal = 0;
        long triangleTotal = 0;
        foreach (var node in nodes)
        {
            node.Camera = null;
            node.Light = null;
            node.PhysicsBody = null;
            node.Constraints = null;
            node.RemoveAllActions();
            node.RemoveAllParticleSystems();
            node.RemoveAllAudioPlayers();
            if (node.Geometry is { } geometry)
            {
                if (clearShaders)
                {
                    geometry.ShaderModifiers = null;
                    geometry.Program = null;
                }
                foreach (var source in geometry.GetGeometrySourcesForSemantic(SCNGeometrySourceSemantic.Vertex))
                {
                    vertexTotal = checked(vertexTotal + (long)source.VectorCount);
                }
                if (vertexTotal > 300_000)
                {
                    throw new InvalidDataException("模型超过 30 万顶点，请先在 Blender 中简化。");
                }
                foreach (var element in geometry.GeometryElements)
                    triangleTotal = checked(triangleTotal + (long)element.PrimitiveCount);
                if (triangleTotal > 500_000)
                    throw new InvalidDataException("模型超过 50 万面，请先简化网格。");
                foreach (var material in geometry.Materials)
                {
                    if (clearShaders)
                    {
                        material.ShaderModifiers = null;
                        material.Program = null;
                    }
                }
            }
            foreach (var nativeKey in node.GetAnimationKeys())
            {
                string key = nativeKey.ToString() ?? string.Empty;
                using var name = new NSString(key);
                var player = node.GetAnimationPlayer(name);
                if (string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(char.IsControl) || player is null)
                {
                    node.RemoveAnimation(name);
                    continue;
                }
                // Nothing is attached to the live renderer during load. Pause before
                // returning the asset so file animations require explicit user input.
                player.Paused = true;
                clips.Add(key);
            }
        }
        if (vertexTotal == 0)
        {
            throw new InvalidDataException("文件中没有可显示的网格。");
        }
        return clips.Take(32).ToList();
    }

    private static void ValidateObj(string path)
    {
        if (new FileInfo(path).Length > 80L * 1024 * 1024)
            throw new InvalidDataException("OBJ 文本超过 80 MB 解析预算；大文件请使用二进制 GLB / VRM 或 USDZ。");
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var value = line.TrimStart();
            if (Regex.IsMatch(value, "^(mtllib|call|csh)(\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                throw new InvalidDataException("此 OBJ 引用了独立材质或外部文件。请导出为单文件 USDZ，或导出不引用 MTL 的 OBJ。");
            }
        }
    }

    private static void ValidateDae(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 80L * 1024 * 1024
        });
        int imageDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= imageDepth) imageDepth = -1;
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "image" && !reader.IsEmptyElement) imageDepth = reader.Depth;
            if (imageDepth >= 0 && reader.LocalName == "init_from" && !reader.IsEmptyElement)
                throw new InvalidDataException("此 DAE 需要独立纹理；单文件导入请使用包含贴图的 USDZ。");
            if (!reader.HasAttributes) continue;
            while (reader.MoveToNextAttribute())
            {
                if (reader.LocalName is "url" or "href" or "src" &&
                    !string.IsNullOrWhiteSpace(reader.Value) && !reader.Value.StartsWith('#'))
                    throw new InvalidDataException("此 DAE 引用了外部文件；请改用包含全部贴图的 USDZ。");
            }
            reader.MoveToElement();
        }
    }

    private static void ValidateUsdz(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is < 1 or > 512)
        {
            throw new InvalidDataException("USDZ 包为空或包含过多文件。");
        }
        var entries = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        string[] allowed = { ".usd", ".usda", ".usdc", ".png", ".jpg", ".jpeg", ".exr" };
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part is ".." or ".") || !entries.Add(name) ||
                (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
            {
                throw new InvalidDataException("USDZ 包含不安全的文件路径。");
            }
            if (name.EndsWith('/'))
            {
                continue;
            }
            if (!allowed.Contains(Path.GetExtension(name).ToLowerInvariant()) || entry.Length != entry.CompressedLength)
            {
                throw new InvalidDataException("请使用标准的未压缩 USDZ，仅包含模型与内置贴图。");
            }
            total = checked(total + entry.Length);
            if (total > MaximumFileBytes)
            {
                throw new InvalidDataException("USDZ 内容超过 1 GB（1024 MB）。");
            }
        }
        if (Path.GetExtension(archive.Entries[0].FullName).ToLowerInvariant() is not (".usd" or ".usda" or ".usdc"))
        {
            throw new InvalidDataException("USDZ 的第一个文件须为 USD 场景。");
        }
        foreach (var entry in archive.Entries.Where(entry => Path.GetExtension(entry.FullName).Equals(".usda", StringComparison.OrdinalIgnoreCase)))
        {
            // The file ceiling is not a promise to fit arbitrary text scenes in
            // RAM. Binary USDC is the recommended format for large USDZ assets.
            if (entry.Length > 16L * 1024 * 1024)
                throw new InvalidDataException("USDZ 文本场景超过 16 MB 解析预算；请导出为二进制 USDC 场景。");
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            string text = reader.ReadToEnd();
            foreach (Match match in Regex.Matches(text, "@([^@]*)@"))
            {
                string dependency = match.Groups[1].Value.Replace('\\', '/');
                if (dependency.Length == 0)
                {
                    continue;
                }
                string parent = Path.GetDirectoryName(entry.FullName)?.Replace('\\', '/') ?? "";
                string resolved = ResolvePackagePath(parent, dependency);
                if (!entries.Contains(resolved))
                {
                    throw new InvalidDataException("USDZ 引用了包外资源，请重新打包全部模型与贴图。");
                }
            }
        }
    }

    private static string ResolvePackagePath(string directory, string dependency)
    {
        if (dependency.StartsWith('/') || dependency.Contains(':') || dependency.Contains('[') || dependency.Contains(']'))
        {
            throw new InvalidDataException("USDZ 包含外部资源引用。");
        }
        var parts = new List<string>();
        foreach (var part in (directory + "/" + dependency).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("USDZ 资源引用超出包内目录。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
