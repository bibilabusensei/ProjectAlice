param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$sdk = dotnet --version
$compiler = Join-Path $dotnetRoot "sdk\$sdk\Roslyn\bincore\csc.dll"
$corePack = Get-ChildItem (Join-Path $dotnetRoot 'packs\Microsoft.NETCore.App.Ref') -Directory |
    Where-Object Name -like '10.*' | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$iosPack = Get-ChildItem (Join-Path $dotnetRoot 'packs') -Directory -Filter 'Microsoft.iOS.Ref.net10.0*' |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $corePack -or -not $iosPack) { throw 'Install the .NET 10 reference and iOS workload packs first.' }
$iosVersion = Get-ChildItem $iosPack.FullName -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$references = @(Get-ChildItem (Join-Path $corePack.FullName 'ref\net10.0') -Filter '*.dll' | ForEach-Object { '/r:' + $_.FullName })
$references += '/r:' + (Join-Path $iosVersion.FullName 'ref\net10.0\Microsoft.iOS.dll')
$sources = @()
foreach ($folder in @('AliceVision.Core', 'AliceVision.iOS')) {
    $sources += @(& rg --files (Join-Path $Workspace $folder) -g '*.cs' -g '!**/bin/**' -g '!**/obj/**')
}
$sources += Join-Path $PSScriptRoot 'iOSReferenceGlobals.cs'
$output = Join-Path $Workspace 'artifacts\ios-reference'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet $compiler '/noconfig' '/nostdlib+' '/langversion:latest' '/nullable:enable' '/unsafe' '/target:exe' "/out:$output\AliceVision.iOS.dll" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Native iOS API reference check failed.' }
Write-Output 'iOS API reference check passed. This is not a native iPhone build; macOS CI is still required.'
