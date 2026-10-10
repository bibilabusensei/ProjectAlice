using System;
using ARKit;
using CoreVideo;

namespace AliceVision.iOS;

/// <summary>Reads actual ARKit LiDAR depth, not a monocular prediction.</summary>
internal static class LidarDiagnostics
{
    public static string Analyze(ARFrame frame, int meshCount)
    {
        using var depth = frame.SmoothedSceneDepth ?? frame.SceneDepth;
        if (depth is null)
        {
            return $"LiDAR 网格块 {meshCount}\n正在等待真实深度帧…";
        }
        using var map = depth.DepthMap;
        // Report presence and dimensions only. A sensor pixel is not directly a
        // screen pixel after orientation and aspect-fill cropping; distance probes
        // will require inverse ARFrame.DisplayTransform and confidence filtering.
        return $"LiDAR 网格块 {meshCount} · 深度 {map.Width}×{map.Height}\n" +
            "真实深度帧已收到 · 30 FPS 渲染\n水平面可为地板或桌面；网格用于真实物体遮挡";
    }
}
