using System;
using ARKit;
using Foundation;
using Metal;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

internal sealed class SceneDelegate : ARSCNViewDelegate
{
    internal const string CharacterAnchorName = "Alice.Companion.Character";
    internal const string TargetAnchorName = "Alice.Companion.Target";
    private const string PlaneNodeName = "Alice.Companion.Plane";
    private readonly VisionViewController _owner;
    public SceneDelegate(VisionViewController owner) => _owner = owner;

    public override SCNNode? GetNode(ISCNSceneRenderer renderer, ARAnchor anchor)
    {
        long epoch = _owner.SessionEpoch;
        if (!_owner.IsSessionEpochCurrent(epoch)) return null;
        var node = new SCNNode();
        if (anchor is ARPlaneAnchor plane)
        {
            var surface = SCNPlane.Create(plane.Extent.X, plane.Extent.Z);
            using var material = new SCNMaterial();
            material.Diffuse.Contents = UIColor.FromRGBA(79, 139, 244, 58);
            material.DoubleSided = true;
            material.WritesToDepthBuffer = false;
            material.LightingModelName = SCNLightingModel.Constant;
            surface.Materials = new[] { material };
            var planeNode = SCNNode.FromGeometry(surface);
            planeNode.Name = PlaneNodeName;
            planeNode.EulerAngles = new SCNVector3(-MathF.PI / 2, 0, 0);
            planeNode.Position = new SCNVector3(plane.Center.X, 0.001f, plane.Center.Z);
            node.AddChildNode(planeNode);
        }
        else if (anchor is ARMeshAnchor mesh)
        {
            node.Geometry = LidarMeshGeometry.Create(mesh.Geometry);
            node.RenderingOrder = -100;
        }
        // UIKit and imported assets are attached on the main thread in DidAddNode.
        if (!_owner.IsSessionEpochCurrent(epoch)) { node.Dispose(); return null; }
        return node;
    }

    public override void DidAddNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        long epoch = _owner.SessionEpoch;
        PublishGeometry(node, anchor, false, epoch);
        if (!_owner.IsSessionEpochCurrent(epoch)) return;
        if (anchor.Name == CharacterAnchorName) _owner.AttachCharacter(node, anchor.Identifier.AsString(), epoch);
        else if (anchor.Name == TargetAnchorName) _owner.AttachTarget(node, anchor.Identifier.AsString(), epoch);
    }

    public override void DidUpdateNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        long epoch = _owner.SessionEpoch;
        if (!_owner.IsSessionEpochCurrent(epoch)) return;
        if (anchor is ARPlaneAnchor plane && node.FindChildNode(PlaneNodeName, false) is { Geometry: SCNPlane geometry } planeNode)
        {
            geometry.Width = plane.Extent.X; geometry.Height = plane.Extent.Z;
            planeNode.Position = new SCNVector3(plane.Center.X, 0.001f, plane.Center.Z);
        }
        else if (anchor is ARMeshAnchor mesh)
        {
            var previous = node.Geometry;
            node.Geometry = LidarMeshGeometry.Create(mesh.Geometry); previous?.Dispose();
        }
        PublishGeometry(node, anchor, false, epoch);
    }

    public override void DidRemoveNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        long epoch = _owner.SessionEpoch;
        PublishGeometry(node, anchor, true, epoch);
        if (anchor.Name is CharacterAnchorName or TargetAnchorName) _owner.AnchorRemoved(anchor.Identifier.AsString(), epoch);
    }

    private void PublishGeometry(SCNNode node, ARAnchor anchor, bool remove, long epoch)
    {
        if (!_owner.IsSessionEpochCurrent(epoch)) return;
        string id = anchor.Identifier.AsString();
        if (anchor is ARPlaneAnchor plane)
        {
            if (remove) { _owner.UpdatePlane(id, true, epoch); return; }
            var boundary = plane.Geometry.GetBoundaryVertices();
            var copied = new SCNVector3[boundary.Length];
            for (int i = 0; i < boundary.Length; i++) copied[i] = new SCNVector3(boundary[i].X, boundary[i].Y, boundary[i].Z);
            if (_owner.IsSessionEpochCurrent(epoch)) _owner.UpdatePlane(id, false, epoch, copied, node.WorldTransform);
        }
        else if (anchor is ARMeshAnchor mesh)
        {
            var copied = remove ? null : CopyTriangleBounds(mesh.Geometry, node.WorldTransform);
            if (_owner.IsSessionEpochCurrent(epoch)) _owner.UpdateMesh(id, remove, epoch, copied);
        }
    }

    private static unsafe VisionViewController.MeshBounds[] CopyTriangleBounds(ARMeshGeometry geometry, SCNMatrix4 transform)
    {
        try
        {
            var source = geometry.Vertices; var faces = geometry.Faces;
            int count = checked((int)source.Count), faceCount = checked((int)faces.Count);
            long offset = checked((long)source.Offset), stride = checked((long)source.Stride);
            int size = checked((int)faces.BytesPerIndex);
            if (count <= 0 || faceCount <= 0 || faceCount > 40000 || source.Format != MTLVertexFormat.Float3 ||
                source.ComponentsPerVector != 3 || stride < 12 || offset < 0 ||
                faces.IndexCountPerPrimitive != 3 || size is not (2 or 4)) return Array.Empty<VisionViewController.MeshBounds>();
            var vertexBuffer = source.Buffer; var faceBuffer = faces.Buffer;
            if (vertexBuffer.Contents == IntPtr.Zero || faceBuffer.Contents == IntPtr.Zero ||
                checked(offset + (count - 1L) * stride + 12) > checked((long)vertexBuffer.Length) ||
                (long)faceCount * 3 * size > checked((long)faceBuffer.Length)) return Array.Empty<VisionViewController.MeshBounds>();
            var world = new SCNVector3[count];
            byte* vertices = (byte*)vertexBuffer.Contents + offset;
            for (int i = 0; i < count; i++)
            {
                float* values = (float*)(vertices + i * stride);
                if (!float.IsFinite(values[0]) || !float.IsFinite(values[1]) || !float.IsFinite(values[2]))
                    return Array.Empty<VisionViewController.MeshBounds>();
                world[i] = SCNVector3.TransformPosition(new SCNVector3(values[0], values[1], values[2]), transform);
            }
            var result = new VisionViewController.MeshBounds[faceCount];
            byte* indices = (byte*)faceBuffer.Contents;
            for (int face = 0; face < faceCount; face++)
            {
                uint Index(int corner) => size == 2
                    ? ((ushort*)indices)[face * 3 + corner] : ((uint*)indices)[face * 3 + corner];
                uint ia = Index(0), ib = Index(1), ic = Index(2);
                if (ia >= count || ib >= count || ic >= count) return Array.Empty<VisionViewController.MeshBounds>();
                var a = world[ia]; var b = world[ib]; var c = world[ic];
                result[face] = new VisionViewController.MeshBounds(
                    new SCNVector3(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z))),
                    new SCNVector3(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z))));
            }
            return result;
        }
        catch (OverflowException) { return Array.Empty<VisionViewController.MeshBounds>(); }
    }

    public override void CameraDidChangeTrackingState(ARSession session, ARCamera camera) => _owner.UpdateTracking(camera, _owner.SessionEpoch);
    public override void WasInterrupted(ARSession session) => _owner.Interrupted(false, _owner.SessionEpoch);
    public override void InterruptionEnded(ARSession session) => _owner.Interrupted(true, _owner.SessionEpoch);
    public override bool ShouldAttemptRelocalization(ARSession session) => true;
    public override void DidFail(ARSession session, NSError error) => _owner.SessionFailed(error, _owner.SessionEpoch);
}
