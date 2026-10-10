using System;
using System.Collections.Generic;
using System.Linq;
using Foundation;
using SceneKit;

namespace AliceVision.iOS;

/// <summary>Local visual poses only. Navigation and AR anchor movement are owned by the controller.</summary>
public static class CharacterMotion
{
    private const string PoseActionKey = "AliceVision.CharacterPose";

    public static bool Play(CharacterAsset asset, SCNNode root, string action)
    {
        if (action == "idle")
        {
            Stop(root);
            if (asset.IsBuiltIn && root.FindChildNode(CharacterFactory.Head, true) is { } head)
            {
                head.RunAction(SCNAction.RepeatActionForever(SCNAction.Sequence(new[]
                {
                    SCNAction.RotateTo(0.025f, 0.055f, 0, 1.8),
                    SCNAction.RotateTo(-0.015f, -0.045f, 0, 1.8)
                })), PoseActionKey);
            }
            return true;
        }

        if (!asset.IsBuiltIn || action is not ("wave" or "walk"))
        {
            return false;
        }
        Stop(root);
        if (action == "wave" && root.FindChildNode(CharacterFactory.RightArm, true) is { } arm)
        {
            var wave = SCNAction.Sequence(new[]
            {
                SCNAction.RotateTo(0, 0, 2.6f, 0.3),
                SCNAction.RepeatAction(SCNAction.Sequence(new[]
                {
                    SCNAction.RotateTo(0, 0, 2.25f, 0.18),
                    SCNAction.RotateTo(0, 0, 2.75f, 0.18)
                }), 4),
                SCNAction.RotateTo(0, 0, 0, 0.3)
            });
            arm.RunAction(wave, PoseActionKey);
            return true;
        }
        if (action == "walk")
        {
            Swing(root, CharacterFactory.LeftLeg, 0.38f);
            Swing(root, CharacterFactory.RightLeg, -0.38f);
            Swing(root, CharacterFactory.LeftArm, -0.22f);
            Swing(root, CharacterFactory.RightArm, 0.22f);
            return true;
        }
        return false;
    }

    public static bool PlayEmbeddedAnimation(CharacterAsset asset, SCNNode root, string clipName)
    {
        if (asset.IsBuiltIn || !asset.AnimationNames.Contains(clipName, StringComparer.Ordinal))
        {
            return false;
        }
        Stop(root);
        using var key = new NSString(clipName);
        bool found = false;
        foreach (var node in Enumerate(root))
        {
            var player = node.GetAnimationPlayer(key);
            if (player is not null)
            {
                // Importer's players remain paused until this explicit user action.
                // A copied animation avoids changing the reusable asset prototype.
                player.Stop();
                player.Paused = false;
                player.Play();
                found = true;
            }
        }
        return found;
    }

    public static void Stop(SCNNode root)
    {
        foreach (var node in Enumerate(root))
        {
            node.RemoveAction(PoseActionKey);
            if (node.Name is CharacterFactory.LeftArm or CharacterFactory.RightArm or
                CharacterFactory.LeftLeg or CharacterFactory.RightLeg or CharacterFactory.Head)
            {
                node.EulerAngles = new SCNVector3();
            }
            foreach (var key in node.GetAnimationKeys())
            {
                using var name = new NSString(key);
                var player = node.GetAnimationPlayer(name);
                if (player is not null)
                {
                    player.Paused = true;
                }
            }
        }
    }

    internal static IEnumerable<SCNNode> Enumerate(SCNNode root)
    {
        var pending = new Stack<SCNNode>();
        pending.Push(root);
        int visited = 0;
        while (pending.Count > 0 && visited < 4096)
        {
            var node = pending.Pop();
            visited++;
            yield return node;
            foreach (var child in node.ChildNodes)
            {
                pending.Push(child);
            }
        }
    }

    private static void Swing(SCNNode root, string name, float angle)
    {
        if (root.FindChildNode(name, true) is { } joint)
        {
            joint.RunAction(SCNAction.RepeatActionForever(SCNAction.Sequence(new[]
            {
                SCNAction.RotateTo(angle, 0, 0, 0.28),
                SCNAction.RotateTo(-angle, 0, 0, 0.28)
            })), PoseActionKey);
        }
    }
}
