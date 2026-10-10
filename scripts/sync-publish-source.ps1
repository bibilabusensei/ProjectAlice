param([string]$PublishDirectory = 'D:\my vs\.projectalice-publish')
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
if (-not $publishRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish clone must be inside the Alice workspace'
}
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot '.git'))) { throw 'Missing publish Git repository' }
$remote = git -C $publishRoot remote get-url origin
if ($LASTEXITCODE -ne 0 -or $remote -ne 'https://github.com/bibilabusensei/ProjectAlice.git') {
    throw 'Unexpected publish repository; refusing to copy files'
}
$files = @('.gitattributes', '.gitignore', 'README.md', 'ProjectAlice.sln', 'ProjectAlice.iOS.sln')
foreach ($directory in @('AliceVision', 'AliceVision.Core', 'AliceVision.Tests', 'AliceVision.iOS', 'AliceVision.Android', '.github', 'scripts', 'docs')) {
    $files += @(& rg --files --hidden (Join-Path $sourceRoot $directory) -g '!**/bin/**' -g '!**/obj/**' -g '!**/__pycache__/**' | ForEach-Object {
        [IO.Path]::GetRelativePath($sourceRoot, $_)
    })
}
foreach ($relative in $files) {
    $source = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
    $target = [IO.Path]::GetFullPath((Join-Path $publishRoot $relative))
    if (-not $source.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($publishRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe publication path' }
    $parent = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
}
Write-Output "Copied $($files.Count) allowlisted source files; no other workspace projects included."
