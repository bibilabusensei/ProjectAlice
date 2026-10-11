using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Foundation;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>Whitelisted bounded local bone poses; never translates an AR anchor or runs model code.</summary>
public static class CharacterMotion
{
    private const string PoseActionKey = "AliceVision.CharacterPose";
    private static readonly string[] Actions = { "idle", "walk", "wave", "wave_left", "wave_right", "greet", "welcome", "nod", "bow", "dance", "look" };
    private static readonly string[] RequiredBones = { "hips", "spine", "head", "leftUpperArm", "leftLowerArm", "leftHand",
        "rightUpperArm", "rightLowerArm", "rightHand", "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot" };
    private static readonly ConditionalWeakTable<SCNNode, RigState> States = new();

    public static IReadOnlyList<string> AllHumanoidActions { get; } = Array.AsReadOnly(Actions);
    public static bool IsKnownAction(string action) => Actions.Contains(action, StringComparer.Ordinal);

    internal static IReadOnlyList<string> ActionsForImportedRig(SCNNode root)
    {
        var influenced = new HashSet<IntPtr>();
        foreach (var node in Enumerate(root))
        {
            if (node.Skinner is not { } skinner) continue;
            foreach (var bone in skinner.Bones)
                for (SCNNode? ancestor = bone; ancestor is not null; ancestor = ancestor.ParentNode)
                {
                    influenced.Add(ancestor.Handle);
                    if (ancestor.Handle == root.Handle) break;
                }
        }
        if (RequiredBones.All(name => root.FindChildNode(CharacterGltfImporter.BonePrefix + name, true) is { } bone && influenced.Contains(bone.Handle)))
            return AllHumanoidActions;
        return new[] { "idle" };
    }

    internal static void Prepare(CharacterAsset asset, SCNNode root)
    {
        if (asset.AvailableActions.Count <= 1) return;
        SCNNode? body = root.FindChildNode(asset.IsBuiltIn ? CharacterFactory.RootRig : CharacterGltfImporter.RigRoot, true);
        if (body is null) return;
        var nodes = new Dictionary<string, SCNNode>(StringComparer.Ordinal);
        if (asset.IsBuiltIn)
        {
            Add("head", CharacterFactory.Head); Add("spine", CharacterFactory.RootRig);
            Add("leftUpperArm", CharacterFactory.LeftArm); Add("rightUpperArm", CharacterFactory.RightArm);
            Add("leftUpperLeg", CharacterFactory.LeftLeg); Add("rightUpperLeg", CharacterFactory.RightLeg);
        }
        else
            foreach (string name in RequiredBones.Concat(new[] { "neck", "chest", "upperChest" }))
                Add(name, CharacterGltfImporter.BonePrefix + name);
        var state = new RigState(body, nodes);
        States.Add(root, state);
        return;

        void Add(string canonical, string name)
        {
            SCNNode? node = body.Name == name ? body : body.FindChildNode(name, true);
            if (node is not null) nodes.Add(canonical, node);
        }
    }

    public static bool Play(CharacterAsset asset, SCNNode root, string action)
    {
        if (!IsKnownAction(action) || !asset.AvailableActions.Contains(action, StringComparer.Ordinal)) return false;
        Stop(root);
        if (!States.TryGetValue(root, out var state)) return action == "idle";
        int generation = Interlocked.Increment(ref state.Generation);
        double duration = action switch
        {
            "idle" => 4.0, "walk" => 0.85, "dance" => 4.5, "bow" => 2.6,
            "nod" => 2.1, "look" => 3.6, "welcome" => 3.3, _ => 3.0
        };
        foreach (var pair in state.Bones)
        {
            string name = pair.Key;
            var pose = pair.Value;
            var clip = SCNAction.CustomAction(duration, (node, elapsed) =>
            {
                lock (state.Sync)
                {
                    if (generation != Volatile.Read(ref state.Generation)) return;
                    double time = Math.Clamp((double)elapsed, 0, duration);
                    var delta = Pose(state, name, action, time, duration);
                    // Rotate in calibrated model axes, preserving every original
                    // bind translation/scale. No root/hip translation is applied.
                    var x = AxisAngle(pose.AxisX, delta.X);
                    var y = AxisAngle(pose.AxisY, delta.Y);
                    var z = AxisAngle(pose.AxisZ, delta.Z);
                    node.Orientation = SCNQuaternion.Normalize(z * y * x * pose.Orientation);
                }
            });
            if (action is "idle" or "walk") clip = SCNAction.RepeatActionForever(clip);
            pose.Node.RunAction(clip, PoseActionKey);
        }
        return true;
    }

    private static SCNVector3 Pose(RigState state, string bone, string action, double time, double duration)
    {
        float t = (float)time;
        float end = action is "idle" or "walk" ? 1 : Smooth(MathF.Min(t / 0.32f, (float)(duration - time) / 0.32f));
        float armRest = bone == "leftUpperArm" ? state.LeftArmRest : bone == "rightUpperArm" ? state.RightArmRest : 0;
        float x = 0, y = 0, z = armRest;
        if (action == "idle")
        {
            if (bone == "head") { x = 0.022f * MathF.Sin(t * 1.57f); y = 0.04f * MathF.Sin(t * 1.57f); }
            if (bone == "spine") z = 0.012f * MathF.Sin(t * 1.57f);
        }
        if (action == "walk")
        {
            float swing = MathF.Sin(t * (2 * MathF.PI / 0.85f));
            if (bone == "leftUpperLeg") x = 0.26f * swing;
            if (bone == "rightUpperLeg") x = -0.26f * swing;
            if (bone == "leftLowerLeg") x = -0.19f * MathF.Max(0, -swing);
            if (bone == "rightLowerLeg") x = -0.19f * MathF.Max(0, swing);
            if (bone == "leftUpperArm") x = -0.18f * swing;
            if (bone == "rightUpperArm") x = 0.18f * swing;
        }
        bool leftWave = action == "wave_left", rightWave = action is "wave" or "wave_right" or "greet";
        bool welcome = action == "welcome";
        if (leftWave || rightWave || welcome)
        {
            if (bone == "leftUpperArm" && (leftWave || welcome)) z = state.LeftArmWave;
            if (bone == "rightUpperArm" && (rightWave || welcome)) z = state.RightArmWave;
            if (bone == "leftLowerArm" && (leftWave || welcome)) z = -0.18f;
            if (bone == "rightLowerArm" && (rightWave || welcome)) z = 0.18f;
            if (bone == "leftHand" && (leftWave || welcome)) z = 0.25f * MathF.Sin(t * 11);
            if (bone == "rightHand" && (rightWave || welcome)) z = 0.25f * MathF.Sin(t * 11);
            // The small built-in rig has no separate hand: oscillate its arm.
            if (!state.Bones.ContainsKey("leftHand") && bone == "leftUpperArm" && leftWave) z += 0.17f * MathF.Sin(t * 11);
            if (!state.Bones.ContainsKey("rightHand") && bone == "rightUpperArm" && rightWave) z += 0.17f * MathF.Sin(t * 11);
            if (bone == "head") { z = welcome ? 0 : -0.07f; x = action == "greet" ? 0.12f * MathF.Sin(t * 5) : 0; }
        }
        if (action == "nod" && bone == "head") x = 0.19f * MathF.Sin(t * 6);
        if (action == "look" && bone == "head") y = 0.32f * MathF.Sin(t * 2 * MathF.PI / (float)duration);
        if (action == "bow")
        {
            float bow = MathF.Sin(t * MathF.PI / (float)duration);
            if (bone == "spine") x = 0.32f * bow;
            if (bone == "head") x = 0.12f * bow;
        }
        if (action == "dance")
        {
            float beat = MathF.Sin(t * 6);
            if (bone == "spine") z = 0.08f * beat;
            if (bone == "head") y = 0.10f * beat;
            if (bone == "leftUpperArm") { z = armRest - 0.45f; x = 0.20f * beat; }
            if (bone == "rightUpperArm") { z = armRest + 0.45f; x = -0.20f * beat; }
            if (bone == "leftUpperLeg") x = 0.09f * beat;
            if (bone == "rightUpperLeg") x = -0.09f * beat;
        }
        // Fade finite gestures back to the comfortable arm-rest pose. The final
        // frame is exact, so Stop/idle never accumulates joint rotation offsets.
        return new SCNVector3(Math.Clamp(x * end, -0.6f, 0.6f), Math.Clamp(y * end, -0.6f, 0.6f),
            Math.Clamp(armRest + (z - armRest) * end, -3.0f, 3.0f));
    }

    private static float Smooth(float value)
    {
        float t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t);
    }
    private static SCNQuaternion AxisAngle(SCNVector3 axis, float angle)
    {
        float sine = MathF.Sin(angle * 0.5f);
        return new SCNQuaternion(axis.X * sine, axis.Y * sine, axis.Z * sine, MathF.Cos(angle * 0.5f));
    }

    public static bool PlayEmbeddedAnimation(CharacterAsset asset, SCNNode root, string clipName)
    {
        if (asset.IsBuiltIn || !asset.AnimationNames.Contains(clipName, StringComparer.Ordinal)) return false;
        Stop(root);
        using var key = new NSString(clipName);
        bool found = false;
        foreach (var node in Enumerate(root))
            if (node.GetAnimationPlayer(key) is { } player)
            {
                player.Stop(); player.Paused = false; player.Play(); found = true;
            }
        return found;
    }

    public static void Stop(SCNNode root)
    {
        if (States.TryGetValue(root, out var state))
            lock (state.Sync)
            {
                Interlocked.Increment(ref state.Generation);
                foreach (var pose in state.Bones.Values)
                {
                    pose.Node.RemoveAction(PoseActionKey);
                    pose.Node.Transform = pose.Transform;
                }
            }
        foreach (var node in Enumerate(root))
        {
            node.RemoveAction(PoseActionKey);
            foreach (var key in node.GetAnimationKeys())
            {
                using var name = new NSString(key);
                if (node.GetAnimationPlayer(name) is { } player) player.Paused = true;
            }
        }
    }

    internal static IEnumerable<SCNNode> Enumerate(SCNNode root)
    {
        var pending = new Stack<SCNNode>(); pending.Push(root);
        int visited = 0;
        while (pending.Count > 0 && visited < 4096)
        {
            var node = pending.Pop(); visited++; yield return node;
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
    }

    private sealed class RigState
    {
        internal readonly object Sync = new();
        internal readonly Dictionary<string, BonePose> Bones;
        internal readonly float LeftArmRest, RightArmRest, LeftArmWave, RightArmWave;
        internal int Generation;
        internal RigState(SCNNode body, Dictionary<string, SCNNode> nodes)
        {
            Bones = nodes.ToDictionary(pair => pair.Key, pair => new BonePose(pair.Value, body), StringComparer.Ordinal);
            LeftArmRest = ArmAngle("left", -0.25f, -0.97f);
            RightArmRest = ArmAngle("right", 0.25f, -0.97f);
            LeftArmWave = ArmAngle("left", -0.45f, 0.89f);
            RightArmWave = ArmAngle("right", 0.45f, 0.89f);

            float ArmAngle(string side, float x, float y)
            {
                if (!nodes.TryGetValue(side + "UpperArm", out var upper)) return 0;
                var direction = new SCNVector3(0, -1, 0);
                var start = body.ConvertPositionFromNode(upper.WorldPosition, null);
                if (nodes.TryGetValue(side + "LowerArm", out var lower))
                {
                    var end = body.ConvertPositionFromNode(lower.WorldPosition, null);
                    direction = end - start;
                }
                var center = nodes.TryGetValue("spine", out var spine)
                    ? body.ConvertPositionFromNode(spine.WorldPosition, null) : new SCNVector3();
                float outward = MathF.Abs(start.X - center.X) > 0.00001f ? MathF.Sign(start.X - center.X)
                    : MathF.Abs(direction.X) > 0.00001f ? MathF.Sign(direction.X) : MathF.Sign(x);
                // Determine the arm's outward side from this avatar, not a naming convention.
                float angle = MathF.Atan2(y, outward * MathF.Abs(x)) - MathF.Atan2(direction.Y, direction.X);
                while (angle > MathF.PI) angle -= 2 * MathF.PI;
                while (angle < -MathF.PI) angle += 2 * MathF.PI;
                return angle;
            }
        }
    }

    private sealed class BonePose
    {
        internal readonly SCNNode Node;
        internal readonly SCNMatrix4 Transform;
        internal readonly SCNQuaternion Orientation;
        internal readonly SCNVector3 AxisX, AxisY, AxisZ;
        internal BonePose(SCNNode node, SCNNode body)
        {
            Node = node; Transform = node.Transform; Orientation = node.Orientation;
            AxisX = Axis(new SCNVector3(1, 0, 0)); AxisY = Axis(new SCNVector3(0, 1, 0)); AxisZ = Axis(new SCNVector3(0, 0, 1));
            SCNVector3 Axis(SCNVector3 value)
            {
                var axis = node.ParentNode?.ConvertVectorFromNode(value, body) ?? value;
                float length = axis.Length;
                if (!float.IsFinite(length) || length < 0.000001f) throw new InvalidOperationException("人形骨骼坐标轴无效。");
                return axis / length;
            }
        }
    }
}
