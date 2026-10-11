param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

# Runs only managed structs from the installed iOS implementation assembly.
# No UIKit objects, cameras, devices or native Apple entry points are created.
& (Join-Path $PSScriptRoot 'check-ios-references.ps1') -Workspace $Workspace
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$runtimePack = Get-ChildItem (Join-Path $dotnetRoot 'packs') -Directory -Filter 'Microsoft.iOS.Runtime.ios.net10.0*' |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $runtimePack) { throw 'Install the iOS runtime workload pack to test its actual managed matrix layout.' }
$runtimeVersion = Get-ChildItem $runtimePack.FullName -Directory |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$runtimeAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $runtimeVersion.FullName 'runtimes\ios\lib\net10.0\Microsoft.iOS.dll'))
$applicationAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $Workspace 'artifacts\ios-reference\AliceVision.iOS.dll'))
$importerType = $applicationAssembly.GetType('AliceVision.iOS.CharacterGltfImporter', $true)
$converter = $importerType.GetMethod('ToSceneMatrix', [Reflection.BindingFlags]'NonPublic,Static')
if (-not $converter) { throw 'The production SceneKit matrix conversion could not be found.' }

function Assert-NativeTransform([Numerics.Matrix4x4]$Matrix, [Numerics.Vector3]$Point, [string]$Name) {
    $native = $converter.Invoke($null, [object[]]@($Matrix))
    # SceneKit uses column vectors: C0*x + C1*y + C2*z + C3*w.
    $actual = [Numerics.Vector3]::new(
        $native.Column0.X * $Point.X + $native.Column1.X * $Point.Y + $native.Column2.X * $Point.Z + $native.Column3.X,
        $native.Column0.Y * $Point.X + $native.Column1.Y * $Point.Y + $native.Column2.Y * $Point.Z + $native.Column3.Y,
        $native.Column0.Z * $Point.X + $native.Column1.Z * $Point.Y + $native.Column2.Z * $Point.Z + $native.Column3.Z)
    $homogeneousW = $native.Column0.W * $Point.X + $native.Column1.W * $Point.Y + $native.Column2.W * $Point.Z + $native.Column3.W
    $expected = [Numerics.Vector3]::Transform($Point, $Matrix)
    if ([Numerics.Vector3]::Distance($actual, $expected) -gt 0.0001 -or [math]::Abs($homogeneousW - 1) -gt 0.0001) {
        throw "$Name failed: native=$actual w=$homogeneousW expected=$expected"
    }
    Write-Output "PASS actual SceneKit bridge: $Name"
}

$point = [Numerics.Vector3]::new(0.3, -0.8, 1.2)
$translation = [Numerics.Matrix4x4]::CreateTranslation(2, 3, 4)
$rotation = [Numerics.Matrix4x4]::CreateFromQuaternion([Numerics.Quaternion]::CreateFromYawPitchRoll(0.7, -0.4, 1.1))
$scale = [Numerics.Matrix4x4]::CreateScale(0.5, 2, 1.3)
$child = [Numerics.Matrix4x4]::Multiply($scale, $rotation)
$child = [Numerics.Matrix4x4]::Multiply($child, [Numerics.Matrix4x4]::CreateTranslation(-0.2, 1.5, 0.9))
$world = [Numerics.Matrix4x4]::Multiply($child, $translation)
$inverseBind = [Numerics.Matrix4x4]::Identity
if (-not [Numerics.Matrix4x4]::Invert($world, [ref]$inverseBind)) { throw 'Synthetic bind matrix is singular.' }
$animated = [Numerics.Matrix4x4]::Multiply($rotation, $world)
$skin = [Numerics.Matrix4x4]::Multiply($inverseBind, $animated)
Assert-NativeTransform ([Numerics.Matrix4x4]::Identity) $point 'identity'
Assert-NativeTransform $translation $point 'translation has no perspective terms'
Assert-NativeTransform $rotation $point 'quaternion rotation'
Assert-NativeTransform $scale $point 'nonuniform scale'
Assert-NativeTransform $world $point 'parent and child hierarchy'
Assert-NativeTransform $inverseBind $point 'inverse bind matrix'
Assert-NativeTransform $skin $point 'animated bone transform'
Write-Output '7/7 production matrix bridge checks passed. This does not replace native rendering or iPhone testing.'
