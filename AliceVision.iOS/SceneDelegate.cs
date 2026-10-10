using ARKit;
using Foundation;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

internal sealed class SceneDelegate : ARSCNViewDelegate
{
    internal const string MarkerAnchorName = "AliceVision.PlacementMarker";
    private const string PlaneNodeName = "AliceVision.DetectedPlane";
    private readonly VisionViewController _owner;

    public SceneDelegate(VisionViewController owner) => _owner = owner;

    public override SCNNode? GetNode(ISCNSceneRenderer renderer, ARAnchor anchor)
    {
        var node = new SCNNode();
        if (anchor is ARPlaneAnchor plane)
        {
            var surface = SCNPlane.Create(plane.Extent.X, plane.Extent.Z);
            var material = new SCNMaterial();
            material.Diffuse.Contents = UIColor.FromRGBA(65, 150, 245, 85);
            material.DoubleSided = true;
            material.WritesToDepthBuffer = false;
            material.LightingModelName = SCNLightingModel.Constant;
            surface.Materials = new[] { material };
            var planeNode = SCNNode.FromGeometry(surface);
            planeNode.Name = PlaneNodeName;
            planeNode.EulerAngles = new SCNVector3(-System.MathF.PI / 2, 0, 0);
            planeNode.Position = new SCNVector3(plane.Center.X, 0.001f, plane.Center.Z);
            node.AddChildNode(planeNode);
        }
        else if (anchor is ARMeshAnchor mesh)
        {
            node.Geometry = LidarMeshGeometry.Create(mesh.Geometry);
            node.RenderingOrder = -100;
        }
        else if (anchor.Name == MarkerAnchorName)
        {
            AddMarker(node);
        }
        return node;
    }

    public override void DidAddNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        if (anchor is ARPlaneAnchor)
        {
            _owner.UpdatePlane(anchor, false);
        }
        else if (anchor is ARMeshAnchor)
        {
            _owner.UpdateMesh(anchor, false);
        }
    }

    public override void DidUpdateNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        if (anchor is ARPlaneAnchor plane && node.FindChildNode(PlaneNodeName, false) is { Geometry: SCNPlane geometry } planeNode)
        {
            geometry.Width = plane.Extent.X;
            geometry.Height = plane.Extent.Z;
            planeNode.Position = new SCNVector3(plane.Center.X, 0.001f, plane.Center.Z);
        }
        else if (anchor is ARMeshAnchor mesh)
        {
            var previous = node.Geometry;
            node.Geometry = LidarMeshGeometry.Create(mesh.Geometry);
            previous?.Dispose();
        }
    }

    public override void DidRemoveNode(ISCNSceneRenderer renderer, SCNNode node, ARAnchor anchor)
    {
        if (anchor is ARPlaneAnchor)
        {
            _owner.UpdatePlane(anchor, true);
        }
        else if (anchor is ARMeshAnchor)
        {
            _owner.UpdateMesh(anchor, true);
        }
    }

    public override void CameraDidChangeTrackingState(ARSession session, ARCamera camera) => _owner.UpdateTracking(camera);

    public override void WasInterrupted(ARSession session) => _owner.Interrupted(false);

    public override void InterruptionEnded(ARSession session) => _owner.Interrupted(true);

    public override bool ShouldAttemptRelocalization(ARSession session) => true;

    public override void DidFail(ARSession session, NSError error) => _owner.SessionFailed(error);

    private static void AddMarker(SCNNode node)
    {
        // A 25 cm high test marker gives an immediate scale and anchoring check.
        // No model downloads, external account, neural model or cloud call is needed.
        var baseNode = CreateColoredNode(SCNCylinder.Create(0.09f, 0.02f), UIColor.FromRGB(34, 201, 152));
        baseNode.Position = new SCNVector3(0, 0.01f, 0);
        node.AddChildNode(baseNode);
        var body = CreateColoredNode(SCNBox.Create(0.10f, 0.13f, 0.075f, 0.018f), UIColor.FromRGB(89, 166, 255));
        body.Position = new SCNVector3(0, 0.09f, 0);
        node.AddChildNode(body);
        var head = CreateColoredNode(SCNSphere.Create(0.05f), UIColor.FromRGB(231, 238, 255));
        head.Position = new SCNVector3(0, 0.20f, 0);
        node.AddChildNode(head);
    }

    private static SCNNode CreateColoredNode(SCNGeometry geometry, UIColor color)
    {
        var material = new SCNMaterial();
        material.Diffuse.Contents = color;
        geometry.Materials = new[] { material };
        return SCNNode.FromGeometry(geometry);
    }
}
