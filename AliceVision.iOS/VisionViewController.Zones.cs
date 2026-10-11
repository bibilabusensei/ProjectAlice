using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AliceVision.Core.Spatial;
using ARKit;
using CoreGraphics;
using SceneKit;
using UIKit;

namespace AliceVision.iOS;

public sealed partial class VisionViewController
{
    private ActivityArea? _activityArea;
    private long _zoneRevision;
    private int _zoneEditing;
    private readonly List<SCNVector3> _draftZone = new();
    private readonly UIView _zoneBar = new();
    private readonly UILabel _zoneInstructions = new();
    private readonly UIButton _zoneFinish = new(UIButtonType.System), _zoneCancel = new(UIButtonType.System);
    private SCNNode? _zoneVisualRoot;
    private SCNVector3? _movingDestination;
    private bool _movingAutonomously;

    private void ConfigureZoneControls()
    {
        _zoneBar.BackgroundColor = UIColor.FromRGB(31, 61, 53).ColorWithAlpha(0.96f);
        _zoneBar.Layer.CornerRadius = 14;
        _zoneBar.Hidden = true;
        View!.AddSubview(_zoneBar);
        ConfigureLabel(_zoneInstructions, 12, 2, _zoneBar);
        _zoneFinish.SetTitle("完成边界", UIControlState.Normal);
        _zoneCancel.SetTitle("取消绘制", UIControlState.Normal);
        _zoneFinish.SetTitleColor(UIColor.White, UIControlState.Normal);
        _zoneCancel.SetTitleColor(UIColor.White, UIControlState.Normal);
        _zoneFinish.TouchUpInside += (_, _) => FinishZoneEditing();
        _zoneCancel.TouchUpInside += (_, _) => CancelZoneEditing();
        _zoneBar.AddSubview(_zoneFinish); _zoneBar.AddSubview(_zoneCancel);
    }

    private void LayoutZoneControls(nfloat x, nfloat top, nfloat width)
    {
        _zoneBar.Frame = new CGRect(x, top + 65, width, 86);
        _zoneInstructions.Frame = new CGRect(10, 5, width - 20, 36);
        _zoneFinish.Frame = new CGRect(8, 43, (width - 24) / 2, 37);
        _zoneCancel.Frame = new CGRect(width / 2 + 4, 43, (width - 24) / 2, 37);
    }

    private void BeginZoneEditing(bool forbidden)
    {
        if (_characterNode is null || _characterPlaneId is null || !TrackingNormal() ||
            !_planeSurfaces.ContainsKey(_characterPlaneId))
        { ShowNotice("先放置角色", "扫描并点击同一块稳定地面，再画活动区域。"); return; }
        if (forbidden && _activityArea is null)
        { ShowNotice("先画绿色区域", "禁止区域必须完整位于活动区域内部。"); return; }
        if (forbidden && _activityArea!.Holes.Count >= 8)
        { ShowNotice("禁区已到上限", "最多 8 个禁止区域；可清除后重新绘制。"); return; }
        StopAutonomy("绘制区域时 AI 暂停。"); StopMotion(); CancelImport();
        _zoneEditing = forbidden ? 2 : 1;
        _draftZone.Clear();
        RefreshZoneEditing(); RenderActivityZones();
    }

    private bool HandleZoneTap(ARRaycastResult hit, string planeId)
    {
        if (_zoneEditing == 0) return false;
        if (planeId != _characterPlaneId)
        { _zoneInstructions.Text = "只能点击角色所在的同一块蓝色平面。"; return true; }
        if (_draftZone.Count >= 12)
        { _zoneInstructions.Text = "最多 12 个角点，请完成边界或取消重画。"; return true; }
        var matrix = hit.WorldTransform;
        var point = new SCNVector3(matrix.Column3.X, matrix.Column3.Y, matrix.Column3.Z);
        if (_draftZone.Count > 0 && (point - _draftZone[^1]).Length < 0.06f)
        { _zoneInstructions.Text = "角点靠得太近，请选至少 6 厘米外的位置。"; return true; }
        _draftZone.Add(point);
        RefreshZoneEditing(); RenderActivityZones();
        return true;
    }

    private void RefreshZoneEditing()
    {
        _zoneBar.Hidden = _zoneEditing == 0;
        _aiLabel.Hidden = _zoneEditing != 0;
        if (_zoneEditing == 0) return;
        _zoneInstructions.Text = (_zoneEditing == 1 ? "绿色活动区" : "红色禁止区") +
            $"：依次点 3～12 个角点，当前 {_draftZone.Count} 个。";
        _zoneFinish.Enabled = _draftZone.Count >= 3;
    }

    private void FinishZoneEditing()
    {
        if (_draftZone.Count < 3 || _characterPlaneId is null || !TrackingNormal())
        { ShowNotice("区域尚未完成", "需要稳定追踪，以及至少 3 个同一地面的角点。"); return; }
        try
        {
            var boundary = _draftZone.Select(point => new Vector2(point.X, point.Z)).ToArray();
            long revision = _zoneRevision + 1;
            var next = _zoneEditing == 1
                ? new ActivityArea(_characterPlaneId, revision, _draftZone[0].Y, boundary)
                : new ActivityArea(_activityArea!.PlaneId, revision, _activityArea.PlaneY,
                    _activityArea.Boundary, _activityArea.Holes.Concat(new[] { (IReadOnlyList<Vector2>)boundary }).ToArray());
            _activityArea = next;
            _zoneRevision = revision;
            _zoneEditing = 0; _draftZone.Clear();
            RefreshZoneEditing(); RenderActivityZones();
            _placementMessage = "区域已保存：绿色可活动，红色禁止；AI 仍需手动启动。";
            RefreshStatus();
        }
        catch (ArgumentException)
        { ShowNotice("边界不合适", "角点不能交叉或重复；禁区须完全在活动区内、互不相交。区域边长最多 10 米。可取消后重画。"); }
        catch (InvalidOperationException)
        { ShowNotice("边界不合适", "区域没有形成有效多边形，请取消后重新绘制。"); }
    }

    private void CancelZoneEditing()
    {
        _zoneEditing = 0; _draftZone.Clear();
        RefreshZoneEditing(); RenderActivityZones();
        RefreshCompanionStatus();
    }

    private void ClearActivityZones()
    {
        StopAutonomy("活动区域已改变，AI 已暂停。"); StopMotion();
        _activityArea = null; ++_zoneRevision;
        _zoneEditing = 0; _draftZone.Clear();
        _zoneVisualRoot?.RemoveFromParentNode(); _zoneVisualRoot?.Dispose(); _zoneVisualRoot = null;
        RefreshZoneEditing(); RefreshCompanionStatus();
    }

    private float CharacterFootRadius()
    {
        float radius = MathF.Max(0.09f, _height * 0.42f);
        var minimum = new SCNVector3(); var maximum = new SCNVector3();
        if (_characterNode?.GetBoundingBox(ref minimum, ref maximum) == true)
        {
            float x = MathF.Max(MathF.Abs(minimum.X), MathF.Abs(maximum.X));
            float z = MathF.Max(MathF.Abs(minimum.Z), MathF.Abs(maximum.Z));
            radius = MathF.Max(radius, MathF.Sqrt(x * x + z * z) + 0.02f);
        }
        return radius;
    }

    private bool ActivitySegmentAllowed(SCNVector3 start, SCNVector3 end, float radius, bool required, out string reason)
    {
        reason = "";
        if (_activityArea is null)
        {
            if (!required) return true;
            reason = "自主移动必须先划定绿色活动区域。"; return false;
        }
        if (_activityArea.PlaneId != _characterPlaneId ||
            Math.Abs(start.Y - _activityArea.PlaneY) > 0.055 || Math.Abs(end.Y - _activityArea.PlaneY) > 0.055 ||
            !_activityArea.SegmentAllowed(new Vector2(start.X, start.Z), new Vector2(end.X, end.Z), radius))
        { reason = "路线越过活动边界或靠近红色禁区，请选择内部目标。"; return false; }
        return true;
    }

    private bool CharacterInsideActivityArea()
    {
        if (_activityArea is null || _characterNode is null || _activityArea.PlaneId != _characterPlaneId) return false;
        var foot = _characterNode.PresentationNode.WorldPosition;
        return Math.Abs(foot.Y - _activityArea.PlaneY) <= 0.055 &&
            _activityArea.ContainsWithClearance(new Vector2(foot.X, foot.Z), CharacterFootRadius());
    }

    private void RenderActivityZones()
    {
        _zoneVisualRoot?.RemoveFromParentNode(); _zoneVisualRoot?.Dispose();
        _zoneVisualRoot = new SCNNode { Name = "Alice.ActivityZones" };
        _sceneView.Scene.RootNode.AddChildNode(_zoneVisualRoot);
        if (_activityArea is not null)
        {
            AddZonePolygon(_activityArea.Boundary.Select(point => new SCNVector3(point.X, (float)_activityArea.PlaneY, point.Y)).ToArray(),
                UIColor.FromRGB(61, 218, 152), true);
            foreach (var hole in _activityArea.Holes)
                AddZonePolygon(hole.Select(point => new SCNVector3(point.X, (float)_activityArea.PlaneY, point.Y)).ToArray(),
                    UIColor.FromRGB(255, 90, 107), true);
        }
        if (_draftZone.Count > 0) AddZonePolygon(_draftZone.ToArray(),
            _zoneEditing == 2 ? UIColor.FromRGB(255, 90, 107) : UIColor.FromRGB(61, 218, 152), false);
    }

    private void AddZonePolygon(SCNVector3[] points, UIColor color, bool closed)
    {
        if (closed && points.Length >= 3)
        {
            using var path = new UIBezierPath();
            path.MoveTo(new CGPoint(points[0].X, -points[0].Z));
            foreach (var point in points.Skip(1)) path.AddLineTo(new CGPoint(point.X, -point.Z));
            path.ClosePath();
            using var shape = SCNShape.Create(path, 0.002f);
            using var fillMaterial = new SCNMaterial();
            fillMaterial.Diffuse.Contents = color.ColorWithAlpha(0.14f);
            fillMaterial.DoubleSided = true; fillMaterial.WritesToDepthBuffer = false;
            fillMaterial.LightingModelName = SCNLightingModel.Constant;
            shape.Materials = new[] { fillMaterial };
            var fill = SCNNode.FromGeometry(shape);
            fill.EulerAngles = new SCNVector3(-MathF.PI / 2, 0, 0);
            fill.Position = new SCNVector3(0, points[0].Y + 0.005f, 0);
            _zoneVisualRoot!.AddChildNode(fill);
        }
        for (int i = 0; i < points.Length; i++)
        {
            using var dot = SCNSphere.Create(0.015f);
            using var material = new SCNMaterial();
            material.Diffuse.Contents = color; material.LightingModelName = SCNLightingModel.Constant;
            dot.Materials = new[] { material };
            var pointNode = SCNNode.FromGeometry(dot);
            pointNode.Position = points[i] + new SCNVector3(0, 0.015f, 0);
            _zoneVisualRoot!.AddChildNode(pointNode);
            if (i == points.Length - 1 && !closed) continue;
            var end = points[(i + 1) % points.Length]; var start = points[i];
            float dx = end.X - start.X, dz = end.Z - start.Z;
            using var line = SCNBox.Create(MathF.Sqrt(dx * dx + dz * dz), 0.008f, 0.008f, 0);
            line.Materials = new[] { material };
            var lineNode = SCNNode.FromGeometry(line);
            lineNode.Position = (start + end) * 0.5f + new SCNVector3(0, 0.01f, 0);
            lineNode.EulerAngles = new SCNVector3(0, -MathF.Atan2(dz, dx), 0);
            _zoneVisualRoot.AddChildNode(lineNode);
        }
    }
}
