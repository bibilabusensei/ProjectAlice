using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using AliceVision.Core.Motion;
using Foundation;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>Bounded, calibrated bone motion. Never translates an AR anchor or executes model code.</summary>
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
            foreach (string name in RequiredBones.Concat(new[] { "neck", "chest", "upperChest" }).Concat(
                from side in new[] { "left", "right" }
                from finger in new[] { "Index", "Middle", "Ring", "Little" }
                from joint in new[] { "Proximal", "Intermediate", "Distal" }
                select side + finger + joint))
                Add(name, CharacterGltfImporter.BonePrefix + name);
        States.Add(root, new RigState(body, nodes));

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
        lock (state.Sync)
        {
            int generation = Interlocked.Increment(ref state.Generation);
            foreach (var pose in state.Bones.Values) pose.Start = ToNumerics(pose.Node.Orientation);
            long started = Stopwatch.GetTimestamp();
            // Evaluate the whole hierarchy in one frame. Separate bone actions
            // can see different parent poses and visibly fight each other.
            var clip = SCNAction.CustomAction(86400, (_, _) =>
            {
                lock (state.Sync)
                {
                    if (generation != Volatile.Read(ref state.Generation)) return;
                    double time = Stopwatch.GetElapsedTime(started).TotalSeconds;
                    ApplyFrame(state, action, time, Stopwatch.GetElapsedTime(state.ClockOrigin).TotalSeconds);
                }
            });
            root.RunAction(SCNAction.RepeatActionForever(clip), PoseActionKey);
        }
        return true;
    }

    private static void ApplyFrame(RigState state, string action, double time, double clock)
    {
        double duration = HumanoidGestureMotion.Duration(action);
        if (time >= duration) action = "idle";
        var wave = IsWave(action) ? HumanoidGestureMotion.SampleWave(time, action == "greet") : default;
        foreach (var pair in state.Bones)
        {
            string name = pair.Key; BonePose pose = pair.Value;
            Vector3 angles = BasePose(state, name, action, time, clock, wave);
            pose.Node.Orientation = SCNQuaternion.Normalize(AxisAngle(pose.AxisZ, angles.Z) *
                AxisAngle(pose.AxisY, angles.Y) * AxisAngle(pose.AxisX, angles.X) * pose.Orientation);
        }
        // Solve in shared model coordinates, then convert absolute orientations
        // through each LIVE parent. Local Z is not the same on different VRMs.
        ApplyArm(state, state.LeftArm, action, time);
        ApplyArm(state, state.RightArm, action, time);
        foreach (var pose in state.Bones.Values) pose.Target = ToNumerics(pose.Node.Orientation);
        float transition = HumanoidGestureMotion.SmoothStep01(time / HumanoidGestureMotion.TransitionDuration(action));
        foreach (var pose in state.Bones.Values)
            pose.Node.Orientation = ToScene(Quaternion.Slerp(pose.Start, pose.Target, transition));
    }

    private static Vector3 BasePose(RigState state, string bone, string action, double time, double clock, WaveMotionSample wave)
    {
        Vector3 angles = HumanoidGestureMotion.SampleBoneAngles(bone, action, time, clock);
        float x = angles.X, y = angles.Y, z = angles.Z;
        float gait = (float)Math.Sin(time * Math.Tau / 1.25);
        // Capsule arms have no elbows/hands. This modest fallback is only for
        // that built-in rig; imported humanoids use the solver below.
        bool left = bone == "leftUpperArm", right = bone == "rightUpperArm";
        if ((left && state.LeftArm is null) || (right && state.RightArm is null))
        {
            float rest = left ? state.LeftArmRest : state.RightArmRest;
            z = rest;
            bool waving = left ? action == "wave_left" : action is "wave" or "wave_right" or "greet";
            if (waving) z += ((left ? state.LeftArmWave : state.RightArmWave) - rest) * wave.Raise
                + (left ? -1 : 1) * wave.WristSwing * 0.30f;
            if (action == "welcome") z += (left ? -1 : 1) * 0.35f *
                HumanoidGestureMotion.GesturePulse(time, 0.18, 1.18, 2.25, 3.75);
            if (action == "walk") x = (left ? -1 : 1) * 0.10f * gait;
        }
        return new Vector3(x, y, z);
    }

    private static void ApplyArm(RigState state, ArmRig? arm, string action, double time)
    {
        if (arm is null) return;
        bool waving = arm.IsLeft ? action == "wave_left" : action is "wave" or "wave_right" or "greet";
        ArmMotionPose pose = waving ? HumanoidArmMotion.SampleWaveArm(arm.MotionRig, time)
            : HumanoidArmMotion.Sample(arm.MotionRig, IsWave(action) ? "idle" : action, time);
        Quaternion body = ToNumerics(state.Body.WorldOrientation);
        arm.Upper.Node.WorldOrientation = ToScene(body * pose.UpperSwing * arm.Upper.InBody);
        arm.Lower.Node.WorldOrientation = ToScene(body * pose.LowerSwing * arm.Lower.InBody);
        arm.Hand.Node.WorldOrientation = ToScene(body * pose.HandInBody);
        foreach (var pair in state.Bones)
        {
            string prefix = arm.IsLeft ? "left" : "right";
            if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                !(pair.Key.Contains("Index", StringComparison.Ordinal) || pair.Key.Contains("Middle", StringComparison.Ordinal) ||
                  pair.Key.Contains("Ring", StringComparison.Ordinal) || pair.Key.Contains("Little", StringComparison.Ordinal))) continue;
            float amount = pair.Key.EndsWith("Proximal", StringComparison.Ordinal) ? 1 :
                pair.Key.EndsWith("Intermediate", StringComparison.Ordinal) ? 0.65f : 0.35f;
            // Finger flex axes must travel WITH the palm, not stay fixed in
            // body space when the hand turns to face the viewer.
            Vector3 curl = Vector3.Transform(arm.CurlAxis, pose.HandInBody * Quaternion.Inverse(arm.Hand.InBody));
            SCNVector3 axis = pair.Value.Node.ParentNode?.ConvertVectorFromNode(
                new SCNVector3(curl.X, curl.Y, curl.Z), state.Body) ?? new SCNVector3(1, 0, 0);
            if (axis.Length > 0.000001f)
                pair.Value.Node.Orientation = SCNQuaternion.Normalize(AxisAngle(axis / axis.Length, pose.FingerCurl * amount) * pair.Value.Orientation);
        }
    }

    private static bool IsWave(string action) => action is "wave" or "wave_left" or "wave_right" or "greet";
    private static SCNQuaternion AxisAngle(SCNVector3 axis, float angle)
    {
        float sine = MathF.Sin(angle * 0.5f);
        return new SCNQuaternion(axis.X * sine, axis.Y * sine, axis.Z * sine, MathF.Cos(angle * 0.5f));
    }
    private static Quaternion ToNumerics(SCNQuaternion value) => Quaternion.Normalize(new Quaternion(value.X, value.Y, value.Z, value.W));
    private static SCNQuaternion ToScene(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    private static Vector3 PositionIn(SCNNode body, SCNNode node)
    {
        var value = body.ConvertPositionFromNode(node.WorldPosition, null);
        return new Vector3(value.X, value.Y, value.Z);
    }

    public static bool PlayEmbeddedAnimation(CharacterAsset asset, SCNNode root, string clipName)
    {
        if (asset.IsBuiltIn || !asset.AnimationNames.Contains(clipName, StringComparer.Ordinal)) return false;
        Stop(root);
        // Authored clips start from bind transforms, not a half-raised arm.
        // This does not move the AR root; imported clip movement remains opt-in.
        if (States.TryGetValue(root, out var state))
            lock (state.Sync)
                foreach (var pose in state.Bones.Values) pose.Node.Transform = pose.Transform;
        using var key = new NSString(clipName);
        bool found = false;
        foreach (var node in Enumerate(root))
            if (node.GetAnimationPlayer(key) is { } player)
            {
                player.Stop(); player.Paused = false; player.Play(); found = true;
            }
        return found;
    }

    internal static void Forget(SCNNode root) { Stop(root); States.Remove(root); }

    public static void Stop(SCNNode root)
    {
        if (States.TryGetValue(root, out var state))
            lock (state.Sync)
            {
                Interlocked.Increment(ref state.Generation);
                root.RemoveAction(PoseActionKey);
                foreach (var pose in state.Bones.Values) pose.Node.RemoveAction(PoseActionKey);
                // Freeze at the current pose. Restoring bind here caused a
                // one-frame T-pose on every menu click/action change.
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
        internal readonly SCNNode Body;
        internal readonly Dictionary<string, BonePose> Bones;
        internal readonly ArmRig? LeftArm, RightArm;
        internal readonly float LeftArmRest, RightArmRest, LeftArmWave, RightArmWave;
        internal readonly long ClockOrigin = Stopwatch.GetTimestamp();
        internal int Generation;
        internal RigState(SCNNode body, Dictionary<string, SCNNode> nodes)
        {
            Body = body;
            Bones = nodes.ToDictionary(pair => pair.Key, pair => new BonePose(pair.Value, body), StringComparer.Ordinal);
            LeftArm = CreateArm("left"); RightArm = CreateArm("right");
            LeftArmRest = ArmAngle("left", -0.25f, -0.97f); RightArmRest = ArmAngle("right", 0.25f, -0.97f);
            LeftArmWave = ArmAngle("left", -0.65f, 0.76f); RightArmWave = ArmAngle("right", 0.65f, 0.76f);

            ArmRig? CreateArm(string side)
            {
                if (!Bones.TryGetValue(side + "UpperArm", out var upper) || !Bones.TryGetValue(side + "LowerArm", out var lower) ||
                    !Bones.TryGetValue(side + "Hand", out var hand)) return null;
                if (!IsAncestor(upper.Node, lower.Node) || !IsAncestor(lower.Node, hand.Node)) return null;
                return new ArmRig(body, side, upper, lower, hand, nodes.GetValueOrDefault("head"), nodes.GetValueOrDefault("hips"));
            }
            float ArmAngle(string side, float x, float y)
            {
                if (!nodes.TryGetValue(side + "UpperArm", out var upper)) return 0;
                Vector3 start = PositionIn(body, upper), direction = new(0, -1, 0);
                if (nodes.TryGetValue(side + "LowerArm", out var lower)) direction = PositionIn(body, lower) - start;
                float center = nodes.TryGetValue("spine", out var spine) ? PositionIn(body, spine).X : 0;
                float outward = MathF.Abs(start.X - center) > 0.00001f ? MathF.Sign(start.X - center) : MathF.Sign(x);
                float angle = MathF.Atan2(y, outward * MathF.Abs(x)) - MathF.Atan2(direction.Y, direction.X);
                return MathF.IEEERemainder(angle, MathF.Tau);
            }
        }
        private static bool IsAncestor(SCNNode parent, SCNNode child)
        {
            for (SCNNode? node = child.ParentNode; node is not null; node = node.ParentNode)
                if (node.Handle == parent.Handle) return true;
            return false;
        }
    }

    private sealed class ArmRig
    {
        internal readonly BonePose Upper, Lower, Hand;
        internal readonly bool IsLeft;
        internal readonly Vector3 Shoulder, Elbow, Wrist, CurlAxis;
        internal readonly float Outward, LowerLength, TotalLength;
        internal readonly ArmMotionRig MotionRig;
        internal ArmRig(SCNNode body, string side, BonePose upper, BonePose lower, BonePose hand, SCNNode? head, SCNNode? hips)
        {
            Upper = upper; Lower = lower; Hand = hand; IsLeft = side == "left";
            Shoulder = PositionIn(body, upper.Node); Elbow = PositionIn(body, lower.Node); Wrist = PositionIn(body, hand.Node);
            float upperLength = (Elbow - Shoulder).Length(); LowerLength = (Wrist - Elbow).Length(); TotalLength = upperLength + LowerLength;
            if (!float.IsFinite(TotalLength) || MathF.Min(upperLength, LowerLength) < 0.000001f)
                throw new InvalidOperationException("人形手臂骨骼长度无效。");
            float center = hips is null ? 0 : PositionIn(body, hips).X;
            float lateral = Shoulder.X - center;
            Outward = MathF.Abs(lateral) > TotalLength * 0.0001f ? MathF.Sign(lateral) : IsLeft ? 1 : -1;
            float headHeight = head is null ? LowerLength * 0.7f : PositionIn(body, head).Y - Shoulder.Y;
            var middle = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "MiddleProximal", true);
            var index = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "IndexProximal", true);
            var little = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "LittleProximal", true);
            Vector3 longitudinal = middle is null ? Wrist - Elbow : PositionIn(body, middle) - Wrist;
            if (longitudinal.LengthSquared() < 1e-12f) longitudinal = Wrist - Elbow;
            Vector3 normal = index is not null && little is not null
                ? Vector3.Cross(longitudinal, PositionIn(body, index) - PositionIn(body, little)) * Outward
                : -Vector3.UnitY;
            MotionRig = new(Shoulder, Elbow, Wrist, Outward, headHeight, longitudinal, normal, hand.InBody);
            Vector3 curl = Vector3.Cross(longitudinal, normal);
            CurlAxis = curl.LengthSquared() > 1e-12f ? Vector3.Normalize(curl) : Vector3.UnitZ;
        }
    }

    private sealed class BonePose
    {
        internal readonly SCNNode Node;
        internal readonly SCNMatrix4 Transform;
        internal readonly SCNQuaternion Orientation;
        internal readonly Quaternion InBody;
        internal readonly SCNVector3 AxisX, AxisY, AxisZ;
        internal Quaternion Start, Target;
        internal BonePose(SCNNode node, SCNNode body)
        {
            Node = node; Transform = node.Transform; Orientation = node.Orientation;
            InBody = Quaternion.Normalize(Quaternion.Inverse(ToNumerics(body.WorldOrientation)) * ToNumerics(node.WorldOrientation));
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
