# Local motion preview

This metadata-only diagnostic evaluates the production `HumanoidArmMotion` and
`HumanoidGestureMotion` code against a VRM's actual humanoid hierarchy. It then
converts arm world rotations into live-parent local rotations and evaluates the
same local-pose handoff duration used by the iOS app.

From the repository root, with .NET 8 or newer and Python/Pillow installed:

```powershell
dotnet run --project scripts/motion-preview-tool/MotionPreview.csproj -- "C:\Models\YourAvatar.vrm" "artifacts/motion-preview/poses.json"
python scripts/render-motion-preview.py artifacts/motion-preview/poses.json artifacts/motion-preview
```

Open the generated `index.html` to see animated front/side previews, including
left/right waves, welcome, head/torso gestures and welcome-to-wave transitions.
The renderer also creates contact sheets and a zoomed first-sweep comparison.

This is a skeleton diagnostic, **not an iOS mesh render**. It does not simulate
textures, mesh skinning, cloth, expressions, frame scheduling or prop attachment.
Abstract drawn fingers indicate palm direction/curl, not the mesh deformation.
The walk cycle is in-place; a preview cannot prove grounded walking in AR.

The exporter reads only the embedded GLB JSON hierarchy. It never loads model
textures or sends network requests. JSON output contains private model bone
proportions: keep all generated outputs in the ignored `artifacts` directory;
do not publish the JSON, model or personal camera footage.
