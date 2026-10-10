param([Parameter(Mandatory)][string]$SourceImage)
$ErrorActionPreference = 'Stop'
# Mechanical resizing/format conversion of the generated original icon.
# Apple app icons must be opaque; emit 24-bit RGB PNGs without an alpha channel.
Add-Type -AssemblyName System.Drawing
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$iconDirectory = Join-Path $workspace 'AliceVision.iOS\Assets.xcassets\AppIcon.appiconset'
New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null
$source = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($SourceImage))
try {
    if ($source.Width -ne $source.Height) { throw 'App icon original must be square.' }
    foreach ($size in @(20, 29, 40, 58, 60, 76, 80, 87, 120, 152, 167, 180, 1024)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::White)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save((Join-Path $iconDirectory "Icon$size.png"), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $source.Dispose() }
Write-Output "Generated opaque app-icon PNGs in $iconDirectory"
