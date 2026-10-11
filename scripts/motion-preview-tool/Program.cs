using System.Numerics;
using System.Text.Json;
using AliceVision.Core.Motion;

// PRIVATE local diagnostic export. Reads only a VRM's JSON hierarchy, never geometry,
// textures or external resources. All choreography comes from production Core.
if (args.Length != 2) throw new ArgumentException("Usage: MotionPreview model.vrm output.json");
string modelPath = Path.GetFullPath(args[0]), outputPath = Path.GetFullPath(args[1]);
if (string.Equals(modelPath, outputPath, StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(Path.GetExtension(outputPath), ".json", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Output must be a separate .json file; never overwrite a model.");
using var input = File.OpenRead(args[0]);
using var reader = new BinaryReader(input);
if (reader.ReadUInt32() != 0x46546c67 || reader.ReadUInt32() != 2) throw new InvalidDataException("Need GLB 2");
reader.ReadUInt32();
int jsonLength = checked((int)reader.ReadUInt32());
if (reader.ReadUInt32() != 0x4e4f534a || jsonLength > 8 * 1024 * 1024) throw new InvalidDataException("Need bounded JSON");
using var document = JsonDocument.Parse(reader.ReadBytes(jsonLength));
var source = document.RootElement;
var rawNodes = source.GetProperty("nodes").EnumerateArray().ToArray();
if (rawNodes.Length is < 1 or > 4096) throw new InvalidDataException("Preview node budget exceeded.");
var parents = Enumerable.Repeat(-1, rawNodes.Length).ToArray();
for (int i = 0; i < rawNodes.Length; i++)
    if (rawNodes[i].TryGetProperty("children", out var children))
        foreach (var child in children.EnumerateArray())
        {
            int index = child.GetInt32();
            if (index < 0 || index >= rawNodes.Length || index == i || parents[index] >= 0)
                throw new InvalidDataException("Invalid, duplicated or multi-parent child.");
            parents[index] = i;
        }
for (int i = 0; i < rawNodes.Length; i++)
{
    var seen = new HashSet<int>();
    for (int index = i; index >= 0; index = parents[index])
        if (!seen.Add(index) || seen.Count > 256) throw new InvalidDataException("Cyclic or excessively deep hierarchy.");
}
bool legacy = source.GetProperty("extensions").TryGetProperty("VRM", out var vrm);
var bones = new Dictionary<string, int>(StringComparer.Ordinal);
if (legacy)
    foreach (var entry in vrm.GetProperty("humanoid").GetProperty("humanBones").EnumerateArray())
        bones.Add(entry.GetProperty("bone").GetString()!, entry.GetProperty("node").GetInt32());
else
    foreach (var entry in source.GetProperty("extensions").GetProperty("VRMC_vrm").GetProperty("humanoid").GetProperty("humanBones").EnumerateObject())
        bones.Add(entry.Name, entry.Value.GetProperty("node").GetInt32());
if (bones.Count > 128 || bones.Values.Any(index => index < 0 || index >= rawNodes.Length))
    throw new InvalidDataException("Invalid humanoid bone mapping.");
Matrix4x4 basis = legacy ? Matrix4x4.CreateRotationY(MathF.PI) : Matrix4x4.Identity;
var nodes = rawNodes.Select(ReadNode).ToArray();
var bind = Evaluate(nodes.Select(n => n.Q).ToArray());
var rigs = new Dictionary<string, ArmMotionRig>();
foreach (string side in new[] { "left", "right" })
{
    Vector3 s = Position(side + "UpperArm"), e = Position(side + "LowerArm"), w = Position(side + "Hand");
    float outward = MathF.Sign(s.X - Position("hips").X);
    Vector3 longitudinal = bones.ContainsKey(side + "MiddleProximal") ? Position(side + "MiddleProximal") - w : w - e;
    Vector3 normal = bones.ContainsKey(side + "IndexProximal") && bones.ContainsKey(side + "LittleProximal")
        ? Vector3.Cross(longitudinal, Position(side + "IndexProximal") - Position(side + "LittleProximal")) * outward : -Vector3.UnitY;
    rigs.Add(side, new(s, e, w, outward, Position("head").Y - s.Y, longitudinal, normal, bind.Rotations[bones[side + "Hand"]]));
}
string[] names = ["hips", "spine", "chest", "upperChest", "neck", "head", "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm", "rightLowerArm", "rightHand", "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes", "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes"];
string[] actions = ["idle", "wave_right", "wave_left", "greet", "welcome", "nod", "bow", "look", "dance", "walk", "welcome_to_wave_left", "welcome_to_wave_right"];
var clips = new List<object>();
foreach (var label in actions)
{
    string action = label.StartsWith("welcome_to_") ? label[11..] : label;
    Quaternion[] start = Targets(label.StartsWith("welcome_to_") ? "welcome" : "idle", label.StartsWith("welcome_to_") ? 2.6 : 0, 20);
    double duration = double.IsFinite(HumanoidGestureMotion.Duration(action)) ? HumanoidGestureMotion.Duration(action) + 0.6 : 4.2;
    var frames = new List<object>();
    for (int frame = 0; frame <= duration * 30; frame++)
    {
        double t = frame / 30.0;
        Quaternion[] target = Targets(action, t, 20 + t);
        float transition = HumanoidGestureMotion.SmoothStep01(t / HumanoidGestureMotion.TransitionDuration(action));
        var mixed = target.Select((q, i) => Quaternion.Normalize(Quaternion.Slerp(start[i], q, transition))).ToArray();
        var pose = Evaluate(mixed);
        string effective = t >= HumanoidGestureMotion.Duration(action) ? "idle" : action;
        var points = names.Where(bones.ContainsKey).ToDictionary(name => name, name => V(pose.Positions[bones[name]]));
        var palms = new Dictionary<string, object>();
        foreach (var (side, rig) in rigs)
        {
            Quaternion delta = pose.Rotations[bones[side + "Hand"]] * Quaternion.Inverse(rig.HandBindInBody);
            Vector3 longAxis = Vector3.Normalize(Vector3.Transform(rig.PalmLongitudinal, delta));
            Vector3 normal = Vector3.Normalize(Vector3.Transform(rig.PalmNormal, delta));
            var arm = ArmSample(side, rig, effective, t);
            palms[side] = new { longitudinal = V(longAxis), normal = V(normal), curl = arm.FingerCurl };
        }
        var head = pose.Rotations[bones["head"]] * Quaternion.Inverse(bind.Rotations[bones["head"]]);
        frames.Add(new { time = t, points, palms, headForward = V(Vector3.Transform(Vector3.UnitZ, head)), headUp = V(Vector3.Transform(Vector3.UnitY, head)) });
    }
    clips.Add(new { action = label, duration, frames });
}
var metadata = new { title = "Production core motion - private VRM skeleton preview (not iOS mesh rendering)", fps = 30,
    modelHumanoidBoneCount = bones.Count, basis = legacy ? "VRM0 to +Z forward" : "VRM1 +Z forward",
    rest = names.Where(bones.ContainsKey).ToDictionary(name => name, name => V(Position(name))), clips };
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(metadata));
Console.WriteLine($"Exported {clips.Count} clips at 30 Hz from production Core; mapped {bones.Count} humanoid bones.");

Quaternion[] Targets(string action, double time, double clock)
{
    if (time >= HumanoidGestureMotion.Duration(action)) action = "idle";
    var local = nodes.Select(n => n.Q).ToArray();
    foreach (var (bone, index) in bones)
    {
        Vector3 angles = HumanoidGestureMotion.SampleBoneAngles(bone, action, time, clock);
        Quaternion parentBind = parents[index] < 0 ? Rotation(basis) : bind.Rotations[parents[index]];
        Quaternion delta = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angles.Z) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, angles.Y) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, angles.X);
        local[index] = Quaternion.Normalize(Quaternion.Inverse(parentBind) * delta * parentBind * nodes[index].Q);
    }
    foreach (var (side, rig) in rigs)
    {
        var arm = ArmSample(side, rig, action, time);
        SetWorld(side + "UpperArm", arm.UpperSwing * bind.Rotations[bones[side + "UpperArm"]]);
        SetWorld(side + "LowerArm", arm.LowerSwing * bind.Rotations[bones[side + "LowerArm"]]);
        SetWorld(side + "Hand", arm.HandInBody);
    }
    return local;

    void SetWorld(string bone, Quaternion world)
    {
        int index = bones[bone];
        var current = Evaluate(local);
        Quaternion parent = parents[index] < 0 ? Rotation(basis) : current.Rotations[parents[index]];
        local[index] = Quaternion.Normalize(Quaternion.Inverse(parent) * world);
    }
}
ArmMotionPose ArmSample(string side, ArmMotionRig rig, string action, double time)
{
    bool waving = side == "left" ? action == "wave_left" : action is "wave" or "wave_right" or "greet";
    return waving ? HumanoidArmMotion.SampleWaveArm(rig, time) : HumanoidArmMotion.Sample(rig, action is "wave" or "wave_left" or "wave_right" or "greet" ? "idle" : action, time);
}
(Vector3[] Positions, Quaternion[] Rotations) Evaluate(Quaternion[] orientations)
{
    Matrix4x4[] world = new Matrix4x4[nodes.Length];
    Vector3[] positions = new Vector3[nodes.Length];
    Quaternion[] rotations = new Quaternion[nodes.Length];
    bool[] done = new bool[nodes.Length];
    for (int i = 0; i < nodes.Length; i++) Visit(i);
    return (positions, rotations);
    void Visit(int i)
    {
        if (done[i]) return;
        if (parents[i] >= 0) Visit(parents[i]);
        world[i] = Matrix4x4.CreateScale(nodes[i].Scale) * Matrix4x4.CreateFromQuaternion(orientations[i]) * Matrix4x4.CreateTranslation(nodes[i].Translation) * (parents[i] < 0 ? basis : world[parents[i]]);
        positions[i] = world[i].Translation;
        rotations[i] = Rotation(world[i]); done[i] = true;
    }
}
Vector3 Position(string bone) => bind.Positions[bones[bone]];
static float[] V(Vector3 value) => [value.X, value.Y, value.Z];
static Quaternion Rotation(Matrix4x4 matrix)
{
    if (!Matrix4x4.Decompose(matrix, out _, out var q, out _)) throw new InvalidDataException("Cannot decompose hierarchy");
    return Quaternion.Normalize(q);
}
static Node ReadNode(JsonElement node)
{
    if (node.TryGetProperty("matrix", out var matrix))
    {
        var f = matrix.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Matrix4x4 m = new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
        if (!Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)) throw new InvalidDataException("Invalid transform");
        return new(scale, rotation, translation);
    }
    return new(Vec("scale", Vector3.One), Quat(), Vec("translation", Vector3.Zero));
    Vector3 Vec(string name, Vector3 fallback)
    {
        if (!node.TryGetProperty(name, out var value)) return fallback;
        var f = value.EnumerateArray().Select(v => v.GetSingle()).ToArray(); return new(f[0], f[1], f[2]);
    }
    Quaternion Quat()
    {
        if (!node.TryGetProperty("rotation", out var value)) return Quaternion.Identity;
        var f = value.EnumerateArray().Select(v => v.GetSingle()).ToArray(); return new(f[0], f[1], f[2], f[3]);
    }
}
readonly record struct Node(Vector3 Scale, Quaternion Q, Vector3 Translation);
