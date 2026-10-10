using System;
using System.Collections.Generic;
using System.Linq;
using Foundation;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>An inert reusable character asset. All SceneKit mutations belong on the main thread.</summary>
public sealed class CharacterAsset : IDisposable
{
    private readonly Func<SCNNode>? _factory;
    private readonly SCNNode? _prototype;
    private bool _disposed;

    internal CharacterAsset(string name, string description, Func<SCNNode> factory)
    {
        DisplayName = name;
        Description = description;
        _factory = factory;
        IsBuiltIn = true;
        AnimationNames = Array.Empty<string>();
    }

    internal CharacterAsset(string name, string description, SCNNode prototype, IEnumerable<string> animationNames)
    {
        DisplayName = name;
        Description = description;
        _prototype = prototype;
        AnimationNames = animationNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    public string DisplayName { get; }
    public string Description { get; }
    public bool IsBuiltIn { get; }
    public bool SupportsWave => IsBuiltIn;
    public bool SupportsWalkAnimation => IsBuiltIn;
    public IReadOnlyList<string> AnimationNames { get; }

    public SCNNode CreateNode(float heightMeters = 0.35f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(heightMeters) || heightMeters is < 0.1f or > 2.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(heightMeters), "角色高度须在 0.1–2 米之间。");
        }

        var content = _factory is not null ? _factory() : ClonePreservingRig(_prototype!);
        if (!IsBuiltIn)
        {
            MakeAnimationPlayersIndependent(content);
        }
        var container = new SCNNode { Name = "AliceVision.Character" };
        var scaled = new SCNNode { Name = "AliceVision.CharacterScale" };
        scaled.AddChildNode(content);
        var minimum = new SCNVector3();
        var maximum = new SCNVector3();
        if (!scaled.GetBoundingBox(ref minimum, ref maximum) ||
            !Finite(minimum) || !Finite(maximum) ||
            (double)maximum.Y - minimum.Y < 0.00001 || (double)maximum.Y - minimum.Y > 1_000_000)
        {
            container.Dispose();
            scaled.Dispose();
            content.Dispose();
            throw new InvalidOperationException("模型没有可显示的三维几何或有效高度。");
        }

        float scale = (float)(heightMeters / ((double)maximum.Y - minimum.Y));
        scaled.Scale = new SCNVector3(scale, scale, scale);
        // Keep the character's center over the anchor and the lowest point at y=0.
        scaled.Position = new SCNVector3(
            -(minimum.X * 0.5f + maximum.X * 0.5f) * scale,
            -minimum.Y * scale,
            -(minimum.Z * 0.5f + maximum.Z * 0.5f) * scale);
        container.AddChildNode(scaled);
        return container;
    }

    private static bool Finite(SCNVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    internal static SCNNode ClonePreservingRig(SCNNode source)
    {
        var clone = source.Clone();
        try
        {
            var map = new Dictionary<IntPtr, SCNNode>();
            var pairs = new List<(SCNNode Source, SCNNode Copy)>();
            var pending = new Stack<(SCNNode Source, SCNNode Copy)>();
            pending.Push((source, clone));
            while (pending.Count > 0)
            {
                if (pairs.Count >= 4096)
                {
                    throw new InvalidOperationException("模型节点过多。");
                }
                var pair = pending.Pop();
                map[pair.Source.Handle] = pair.Copy;
                pairs.Add(pair);
                var originalChildren = pair.Source.ChildNodes;
                var clonedChildren = pair.Copy.ChildNodes;
                if (originalChildren.Length != clonedChildren.Length)
                {
                    throw new InvalidOperationException("模型的节点层级无法复制。");
                }
                for (int i = 0; i < originalChildren.Length; i++)
                {
                    pending.Push((originalChildren[i], clonedChildren[i]));
                }
            }
            foreach (var pair in pairs)
            {
                if (pair.Source.Skinner is not { } skinner)
                {
                    continue;
                }
                var bones = new SCNNode[skinner.Bones.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    if (!map.TryGetValue(skinner.Bones[i].Handle, out var bone))
                    {
                        throw new InvalidOperationException("骨骼在模型层级之外，请将角色和骨骼一同导出。");
                    }
                    bones[i] = bone;
                }
                // clone() shares attachments. Rebind the skinner to this copied
                // hierarchy so embedded bone clips affect the visible character.
                var baseGeometry = skinner.BaseGeometry ?? pair.Source.Geometry ??
                    throw new InvalidOperationException("骨骼蒙皮缺少基础网格。");
                var inverseTransforms = skinner.BoneInverseBindTransforms ??
                    Enumerable.Repeat(SCNMatrix4.Identity, bones.Length).ToArray();
                using var copiedSkinner = SCNSkinner.Create(baseGeometry, bones,
                    inverseTransforms, skinner.BoneWeights, skinner.BoneIndices);
                copiedSkinner.BaseGeometryBindTransform = skinner.BaseGeometryBindTransform;
                if (skinner.Skeleton is { } skeleton && map.TryGetValue(skeleton.Handle, out var copiedSkeleton))
                {
                    copiedSkinner.Skeleton = copiedSkeleton;
                }
                pair.Copy.Skinner = copiedSkinner;
            }
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    private static void MakeAnimationPlayersIndependent(SCNNode root)
    {
        foreach (var node in CharacterMotion.Enumerate(root))
        {
            foreach (var key in node.GetAnimationKeys())
            {
                using var name = new NSString(key);
                var original = node.GetAnimationPlayer(name);
                if (original is null)
                {
                    continue;
                }
                using var animation = (SCNAnimation)original.Animation.Copy();
                animation.RemovedOnCompletion = false;
                animation.RepeatCount = 1;
                animation.UsesSceneTimeBase = false;
                animation.AnimationDidStart = null;
                animation.AnimationDidStop = null;
                animation.AnimationEvents = Array.Empty<SCNAnimationEvent>();
                using var player = SCNAnimationPlayer.FromAnimation(animation);
                player.Paused = true;
                node.RemoveAnimation(name);
                node.AddAnimation(player, name);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _prototype?.Dispose();
        }
    }
}
