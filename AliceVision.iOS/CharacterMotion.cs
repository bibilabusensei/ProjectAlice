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
            foreach (string name in RequiredBones.Concat(new[] { "neck", "chest", "upperChest" }))
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
        double duration = Duration(action);
        if (time >= duration) action = "idle";
        var idle = HumanoidGestureMotion.SampleIdle(clock);
        var wave = IsWave(action) ? HumanoidGestureMotion.SampleWave(time, action == "greet") : default;
        float envelope = action is "idle" or "walk" ? 1 : Envelope(time, duration);
        foreach (var pair in state.Bones)
        {
            string name = pair.Key; BonePose pose = pair.Value;
            Vector3 angles = BasePose(state, name, action, time, envelope, idle, wave);
            pose.Node.Orientation = SCNQuaternion.Normalize(AxisAngle(pose.AxisZ, angles.Z) *
                AxisAngle(pose.AxisY, angles.Y) * AxisAngle(pose.AxisX, angles.X) * pose.Orientation);
        }
        // Solve in shared model coordinates, then convert absolute orientations
        // through each LIVE parent. Local Z is not the same on different VRMs.
        ApplyArm(state, state.LeftArm, action, time, envelope, wave);
        ApplyArm(state, state.RightArm, action, time, envelope, wave);
        foreach (var pose in state.Bones.Values) pose.Target = ToNumerics(pose.Node.Orientation);
        float transition = HumanoidGestureMotion.SmoothStep01(time / 0.50);
        foreach (var pose in state.Bones.Values)
            pose.Node.Orientation = ToScene(Quaternion.Slerp(pose.Start, pose.Target, transition));
    }

    private static Vector3 BasePose(RigState state, string bone, string action, double time, float envelope,
        IdleMotionSample idle, WaveMotionSample wave)
    {
        float x = 0, y = 0, z = 0;
        if (bone == "head") { x = 0.003f * idle.Breathing; y = idle.HeadYaw; z = idle.HeadTilt; }
        if (bone == "spine") { x = 0.005f * idle.Breathing; z = 0.003f * idle.WeightShift; }
        float gait = (float)Math.Sin(time * Math.Tau / 1.05);
        if (action == "walk")
        {
            if (bone == "leftUpperLeg") x = 0.20f * gait;
            if (bone == "rightUpperLeg") x = -0.20f * gait;
            if (bone == "leftLowerLeg") x = -0.16f * MathF.Max(0, -gait);
            if (bone == "rightLowerLeg") x = -0.16f * MathF.Max(0, gait);
        }
        if (IsWave(action) && bone == "head") { x += wave.HeadNod; z += wave.HeadTilt; }
        if (action == "nod" && bone == "head") x += 0.14f * Pulse(time, 0.25, 0.85, 1.10, 1.90);
        if (action == "bow")
        {
            float bend = Pulse(time, 0.20, 1.00, 1.30, 2.50);
            if (bone == "spine") x += 0.22f * bend;
            if (bone == "head") x += 0.07f * bend;
        }
        if (action == "look" && bone == "head") y += 0.20f * Pulse(time, 0.25, 1.00, 1.80, 3.10);
        if (action == "dance")
        {
            float beat = (float)Math.Sin(time * 3.6);
            if (bone == "spine") z += 0.035f * beat * envelope;
            if (bone == "head") y += 0.025f * (float)Math.Sin(time * 3.6 - 0.4) * envelope;
            if (bone == "leftUpperLeg") x = 0.045f * beat * envelope;
            if (bone == "rightUpperLeg") x = -0.045f * beat * envelope;
        }
        // Capsule arms have no elbows/hands. This modest fallback is only for
        // that built-in rig; imported humanoids use the solver below.
        bool left = bone == "leftUpperArm", right = bone == "rightUpperArm";
        if ((left && state.LeftArm is null) || (right && state.RightArm is null))
        {
            float rest = left ? state.LeftArmRest : state.RightArmRest;
            z = rest;
            bool waving = left ? action is "wave_left" or "welcome" : action is "wave" or "wave_right" or "greet" or "welcome";
            if (waving) z += ((left ? state.LeftArmWave : state.RightArmWave) - rest) * wave.Raise + wave.WristSwing * 0.30f;
            if (action == "walk") x = (left ? -1 : 1) * 0.10f * gait;
        }
        return new Vector3(x, y, z);
    }

    private static void ApplyArm(RigState state, ArmRig? arm, string action, double time, float envelope, WaveMotionSample wave)
    {
        if (arm is null) return;
        bool waving = arm.IsLeft ? action is "wave_left" or "welcome" : action is "wave" or "wave_right" or "greet" or "welcome";
        float raise = waving ? wave.Raise : 0;
        Vector3 shoulder = PositionIn(state.Body, arm.Upper.Node);
        Vector3 offset = shoulder - arm.Shoulder;
        Vector3 target = arm.RestTarget + offset;
        if (action == "walk")
            target.Z += (arm.IsLeft ? -1 : 1) * 0.14f * arm.TotalLength * (float)Math.Sin(time * Math.Tau / 1.05);
        if (action == "dance")
        {
            target.X += arm.Outward * arm.TotalLength * 0.13f * envelope;
            target.Z += (arm.IsLeft ? 1 : -1) * arm.TotalLength * 0.06f * (float)Math.Sin(time * 3.6 - 0.3) * envelope;
        }
        SolvedArm rest = TwoBoneArmPose.Solve(shoulder, arm.Elbow + offset, arm.Wrist + offset, target, arm.ElbowHint + offset);
        Quaternion upperSwing = rest.UpperSwing, lowerSwing = rest.LowerSwing;
        if (waving)
        {
            Vector3 raisedTarget = arm.RaisedTarget + offset;
            raisedTarget.X += arm.Outward * arm.LowerLength * wave.ForearmSwing;
            SolvedArm lifted = TwoBoneArmPose.Solve(shoulder, arm.Elbow + offset, arm.Wrist + offset, raisedTarget, arm.ElbowHint + offset);
            // A straight wrist path crosses the elbow hint's direction on some
            // rigs and flips the bend plane. Blend solved rotations instead;
            // joint translations/lengths stay intact throughout the lift.
            var swings = TwoBoneArmPose.BlendSwings(rest, lifted, raise);
            upperSwing = swings.Upper; lowerSwing = swings.Lower;
        }
        Quaternion body = ToNumerics(state.Body.WorldOrientation);
        arm.Upper.Node.WorldOrientation = ToScene(body * upperSwing * arm.Upper.InBody);
        arm.Lower.Node.WorldOrientation = ToScene(body * lowerSwing * arm.Lower.InBody);
        Quaternion relaxedHand = Quaternion.Normalize(rest.LowerSwing * arm.Hand.InBody);
        Quaternion liftedHand = Quaternion.Normalize(arm.PalmAlignment * arm.Hand.InBody);
        if (waving) liftedHand = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, wave.WristSwing) * liftedHand;
        arm.Hand.Node.WorldOrientation = ToScene(body * Quaternion.Slerp(relaxedHand, liftedHand, raise));
    }

    private static bool IsWave(string action) => action is "wave" or "wave_left" or "wave_right" or "greet" or "welcome";
    private static double Duration(string action) => action switch
    {
        "idle" or "walk" => double.PositiveInfinity, "dance" => 4.5, "bow" => 2.8,
        "nod" => 2.2, "look" => 3.4, _ => HumanoidGestureMotion.WaveDurationSeconds
    };
    private static float Envelope(double time, double duration) =>
        HumanoidGestureMotion.SmoothStep01(time / 0.5) * HumanoidGestureMotion.SmoothStep01((duration - time) / 0.6);
    private static float Pulse(double time, double start, double up, double hold, double end) =>
        time <= start || time >= end ? 0 : time < up ? HumanoidGestureMotion.SmoothStep01((time - start) / (up - start)) :
        time <= hold ? 1 : 1 - HumanoidGestureMotion.SmoothStep01((time - hold) / (end - hold));
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
        internal readonly Vector3 Shoulder, Elbow, Wrist, RestTarget, RaisedTarget, ElbowHint;
        internal readonly float Outward, LowerLength, TotalLength;
        internal readonly Quaternion PalmAlignment;
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
            RestTarget = Shoulder + Vector3.Normalize(new Vector3(Outward * 0.15f, -0.98f, 0.10f)) * TotalLength * 0.985f;
            float headHeight = head is null ? LowerLength * 0.7f : PositionIn(body, head).Y - Shoulder.Y;
            RaisedTarget = Shoulder + new Vector3(Outward * upperLength * 0.62f,
                Math.Clamp(headHeight + LowerLength * 0.25f, LowerLength * 0.6f, LowerLength * 1.1f), upperLength * 0.30f);
            ElbowHint = Shoulder + new Vector3(Outward * upperLength * 0.8f, -upperLength * 0.65f, upperLength * 0.20f);
            var middle = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "MiddleProximal", true);
            var index = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "IndexProximal", true);
            var little = body.FindChildNode(CharacterGltfImporter.BonePrefix + side + "LittleProximal", true);
            Vector3 longitudinal = middle is null ? Wrist - Elbow : PositionIn(body, middle) - Wrist;
            if (longitudinal.LengthSquared() < 1e-12f) longitudinal = Wrist - Elbow;
            Vector3 normal = index is not null && little is not null
                ? Vector3.Cross(longitudinal, PositionIn(body, index) - PositionIn(body, little)) * Outward
                : -Vector3.UnitY;
            PalmAlignment = TwoBoneArmPose.AlignBasis(longitudinal, normal, Vector3.UnitY, Vector3.UnitZ);
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
