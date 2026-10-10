using SceneKit;
using UIKit;

namespace AliceVision.iOS;

/// <summary>Creates an original chibi companion with a small articulated primitive rig.</summary>
public static class CharacterFactory
{
    internal const string RootRig = "AliceVision.Rig";
    internal const string LeftArm = "AliceVision.Rig.LeftArm";
    internal const string RightArm = "AliceVision.Rig.RightArm";
    internal const string LeftLeg = "AliceVision.Rig.LeftLeg";
    internal const string RightLeg = "AliceVision.Rig.RightLeg";
    internal const string Head = "AliceVision.Rig.Head";

    public static CharacterAsset CreateDefault() => new(
        "小光 · 原创空间伙伴",
        "内置原创低多边形角色，支持关节挥手、原地迈步与待机。无需下载模型。",
        BuildRig);

    private static SCNNode BuildRig()
    {
        var root = new SCNNode { Name = RootRig };
        var suit = UIColor.FromRGB(64, 151, 246);
        var skin = UIColor.FromRGB(254, 225, 211);
        var hair = UIColor.FromRGB(78, 85, 141);
        var dark = UIColor.FromRGB(33, 47, 72);
        var white = UIColor.FromRGB(247, 249, 255);

        var torso = Part(SCNCapsule.Create(0.12f, 0.31f), suit, new SCNVector3(0, 0.43f, 0));
        root.AddChildNode(torso);
        var collar = Part(SCNTorus.Create(0.085f, 0.023f), white, new SCNVector3(0, 0.55f, 0));
        root.AddChildNode(collar);
        var coat = Part(SCNCone.Create(0.155f, 0.095f, 0.16f), suit, new SCNVector3(0, 0.315f, 0));
        root.AddChildNode(coat);
        var button = Part(SCNSphere.Create(0.022f), white, new SCNVector3(0, 0.435f, 0.117f));
        root.AddChildNode(button);

        root.AddChildNode(Leg(LeftLeg, -0.068f, dark, white));
        root.AddChildNode(Leg(RightLeg, 0.068f, dark, white));
        root.AddChildNode(Arm(LeftArm, -0.137f, suit, skin));
        root.AddChildNode(Arm(RightArm, 0.137f, suit, skin));

        var head = new SCNNode { Name = Head, Position = new SCNVector3(0, 0.615f, 0) };
        var face = Part(SCNSphere.Create(0.202f), skin, new SCNVector3(0, 0.105f, 0));
        face.Scale = new SCNVector3(1, 1.03f, 0.93f);
        head.AddChildNode(face);
        var hairBack = Part(SCNSphere.Create(0.213f), hair, new SCNVector3(0, 0.135f, -0.055f));
        hairBack.Scale = new SCNVector3(1.03f, 1.06f, 0.8f);
        head.AddChildNode(hairBack);
        for (int i = -2; i <= 2; i++)
        {
            var fringe = Part(SCNCapsule.Create(0.033f, 0.155f), hair,
                new SCNVector3(i * 0.054f, 0.24f - System.MathF.Abs(i) * 0.017f, 0.134f));
            fringe.EulerAngles = new SCNVector3(0, 0, -i * 0.13f);
            head.AddChildNode(fringe);
        }
        head.AddChildNode(Eye(-0.072f, dark, white));
        head.AddChildNode(Eye(0.072f, dark, white));
        var mouth = Part(SCNCapsule.Create(0.008f, 0.036f), dark, new SCNVector3(0, 0.052f, 0.187f));
        mouth.EulerAngles = new SCNVector3(0, 0, System.MathF.PI / 2);
        head.AddChildNode(mouth);
        var clip = Part(SCNBox.Create(0.055f, 0.025f, 0.02f, 0.007f), UIColor.FromRGB(255, 194, 68),
            new SCNVector3(-0.151f, 0.24f, 0.105f));
        head.AddChildNode(clip);
        root.AddChildNode(head);
        return root;
    }

    private static SCNNode Arm(string name, float x, UIColor sleeve, UIColor skin)
    {
        var joint = new SCNNode { Name = name, Position = new SCNVector3(x, 0.505f, 0) };
        joint.AddChildNode(Part(SCNCapsule.Create(0.036f, 0.155f), sleeve, new SCNVector3(0, -0.066f, 0)));
        joint.AddChildNode(Part(SCNSphere.Create(0.04f), skin, new SCNVector3(0, -0.149f, 0)));
        return joint;
    }

    private static SCNNode Leg(string name, float x, UIColor leggings, UIColor shoes)
    {
        var joint = new SCNNode { Name = name, Position = new SCNVector3(x, 0.24f, 0) };
        joint.AddChildNode(Part(SCNCapsule.Create(0.042f, 0.18f), leggings, new SCNVector3(0, -0.079f, 0)));
        var foot = Part(SCNBox.Create(0.091f, 0.057f, 0.14f, 0.023f), shoes, new SCNVector3(0, -0.209f, 0.025f));
        joint.AddChildNode(foot);
        return joint;
    }

    private static SCNNode Eye(float x, UIColor iris, UIColor white)
    {
        var eye = new SCNNode { Position = new SCNVector3(x, 0.112f, 0.177f) };
        var oval = Part(SCNSphere.Create(0.04f), white, new SCNVector3());
        oval.Scale = new SCNVector3(0.75f, 1, 0.25f);
        eye.AddChildNode(oval);
        var pupil = Part(SCNSphere.Create(0.025f), iris, new SCNVector3(0, -0.003f, 0.009f));
        pupil.Scale = new SCNVector3(0.75f, 1, 0.30f);
        eye.AddChildNode(pupil);
        eye.AddChildNode(Part(SCNSphere.Create(0.008f), white, new SCNVector3(-0.006f, 0.009f, 0.017f)));
        return eye;
    }

    private static SCNNode Part(SCNGeometry geometry, UIColor color, SCNVector3 position)
    {
        using var material = new SCNMaterial();
        material.Diffuse.Contents = color;
        material.Roughness.Contents = new Foundation.NSNumber(0.7f);
        geometry.Materials = new[] { material };
        var node = SCNNode.FromGeometry(geometry);
        node.Position = position;
        return node;
    }
}
