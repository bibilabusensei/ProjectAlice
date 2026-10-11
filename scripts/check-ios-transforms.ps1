param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

# Runs only managed matrix/quaternion structs from the installed iOS implementation assembly.
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

$motionType = $applicationAssembly.GetType('AliceVision.iOS.CharacterMotion', $true)
$toNumerics = $motionType.GetMethod('ToNumerics', [Reflection.BindingFlags]'NonPublic,Static')
$toScene = $motionType.GetMethod('ToScene', [Reflection.BindingFlags]'NonPublic,Static')
if (-not $toNumerics -or -not $toScene) { throw 'The production motion quaternion conversions could not be found.' }
$quaternionType = $runtimeAssembly.GetType('SceneKit.SCNQuaternion', $true)
$vectorType = $runtimeAssembly.GetType('SceneKit.SCNVector3', $true)
$matrixType = $runtimeAssembly.GetType('SceneKit.SCNMatrix4', $true)
$nativeNormalize = $quaternionType.GetMethod('Normalize', [Type[]]@($quaternionType))
$nativeMultiply = $quaternionType.GetMethod('Multiply', [Type[]]@($quaternionType, $quaternionType))
$nativeAxisAngle = $quaternionType.GetMethod('ToAxisAngle', [Type[]]@())
$nativeRotation = $matrixType.GetMethod('CreateFromAxisAngle', [Type[]]@($vectorType, [float]))
$nativeTransform = $vectorType.GetMethod('TransformVector', [Type[]]@($vectorType, $matrixType))
$nativeVectorConstructor = $vectorType.GetConstructor([Type[]]@([float], [float], [float]))
if (-not $nativeNormalize -or -not $nativeMultiply -or -not $nativeAxisAngle -or
    -not $nativeRotation -or -not $nativeTransform -or -not $nativeVectorConstructor) {
    throw 'Installed SceneKit managed quaternion/vector operations could not be found.'
}

function Assert-QuaternionRotation($Native, [Numerics.Quaternion]$Expected, [string]$Name) {
    $expectedUnit = [Numerics.Quaternion]::Normalize($Expected)
    $actualUnit = $toNumerics.Invoke($null, [object[]]@($Native))
    # q and -q describe the same orientation; compare the shorter component arc.
    $positiveDistance = [Numerics.Quaternion]::Subtract($actualUnit, $expectedUnit).Length()
    $negativeDistance = [Numerics.Quaternion]::Add($actualUnit, $expectedUnit).Length()
    if ([math]::Min($positiveDistance, $negativeDistance) -gt 0.00001) {
        throw "$Name failed: quaternion native=$actualUnit expected=$expectedUnit"
    }
    $normalizedNative = $nativeNormalize.Invoke($null, [object[]]@($Native))
    $axisAngle = $nativeAxisAngle.Invoke($normalizedNative, [object[]]@())
    $axis = $nativeVectorConstructor.Invoke([object[]]@([float]$axisAngle.X, [float]$axisAngle.Y, [float]$axisAngle.Z))
    $nativeMatrix = $nativeRotation.Invoke($null, [object[]]@($axis, [float]$axisAngle.W))
    # Exercise actual SceneKit angle->matrix->vector code rather than converting
    # back to Numerics and using Numerics for both sides of the assertion.
    foreach ($testPoint in @([Numerics.Vector3]::UnitX, [Numerics.Vector3]::UnitY, [Numerics.Vector3]::UnitZ, $point)) {
        $nativePoint = $nativeVectorConstructor.Invoke([object[]]@([float]$testPoint.X, [float]$testPoint.Y, [float]$testPoint.Z))
        $rotated = $nativeTransform.Invoke($null, [object[]]@($nativePoint, $nativeMatrix))
        $actualPoint = [Numerics.Vector3]::new($rotated.X, $rotated.Y, $rotated.Z)
        $expectedPoint = [Numerics.Vector3]::Transform($testPoint, $expectedUnit)
        if ([Numerics.Vector3]::Distance($actualPoint, $expectedPoint) -gt 0.0001) {
            throw "$Name failed: native rotation=$actualPoint expected=$expectedPoint"
        }
    }
    Write-Output "PASS actual SceneKit quaternion bridge: $Name"
}

function Assert-QuaternionRoundTrip([Numerics.Quaternion]$Quaternion, [string]$Name) {
    $native = $toScene.Invoke($null, [object[]]@($Quaternion))
    foreach ($component in @('X', 'Y', 'Z', 'W')) {
        if ([math]::Abs($native.$component - $Quaternion.$component) -gt 0.000001) {
            throw "$Name failed: ToScene changed quaternion component $component"
        }
    }
    Assert-QuaternionRotation $native $Quaternion $Name
}

function Assert-QuaternionComposition([Numerics.Quaternion]$Left, [Numerics.Quaternion]$Right, [string]$Name) {
    $nativeLeft = $toScene.Invoke($null, [object[]]@($Left))
    $nativeRight = $toScene.Invoke($null, [object[]]@($Right))
    $product = $nativeMultiply.Invoke($null, [object[]]@($nativeLeft, $nativeRight))
    $product = $nativeNormalize.Invoke($null, [object[]]@($product))
    $expectedProduct = [Numerics.Quaternion]::Normalize([Numerics.Quaternion]::Multiply($Left, $Right))
    Assert-QuaternionRotation $product $expectedProduct $Name
}

$leftBind = [Numerics.Quaternion]::CreateFromYawPitchRoll(0.13, -0.21, 0.68)
$rightBind = [Numerics.Quaternion]::CreateFromYawPitchRoll(-0.17, 0.25, -0.72)
$leftSwing = [Numerics.Quaternion]::CreateFromYawPitchRoll(0.24, 0.44, -0.83)
$rightSwing = [Numerics.Quaternion]::CreateFromYawPitchRoll(-0.24, 0.44, 0.83)
$bodyRotation = [Numerics.Quaternion]::CreateFromYawPitchRoll(0.93, -0.08, 0.16)
$parentRotation = [Numerics.Quaternion]::CreateFromYawPitchRoll(-0.31, 0.52, 0.14)
$childRotation = [Numerics.Quaternion]::CreateFromYawPitchRoll(0.17, -0.28, -0.63)
Assert-QuaternionRoundTrip ([Numerics.Quaternion]::Identity) 'identity round trip'
Assert-QuaternionRoundTrip $leftBind 'rotated left bind orientation'
Assert-QuaternionRoundTrip $rightBind 'rotated right bind orientation'
Assert-QuaternionRoundTrip ([Numerics.Quaternion]::Multiply($leftBind, [float]2.3)) 'ToNumerics normalizes non-unit input'
Assert-QuaternionComposition $leftSwing $leftBind 'left swing multiplied by rotated bind'
Assert-QuaternionComposition $rightSwing $rightBind 'right swing multiplied by rotated bind'
Assert-QuaternionComposition $parentRotation $childRotation 'parent times child with noncommuting rotations'
Assert-QuaternionComposition $bodyRotation ([Numerics.Quaternion]::Normalize([Numerics.Quaternion]::Multiply($parentRotation, $childRotation))) 'body times parent-child hierarchy'
Write-Output '7/7 production matrix and 8/8 quaternion bridge checks passed. These do not replace native rendering or iPhone testing.'
