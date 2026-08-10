param(
    [string]$Version
)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Version)) {
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Cargo.toml') -Raw
    $match = [regex]::Match($manifest, '(?m)^version\s*=\s*"([^"]+)"')
    if (-not $match.Success) { throw 'Could not read the package version from Cargo.toml.' }
    $Version = $match.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid release version: $Version"
}

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$publish = Join-Path $artifacts 'publish-win-x64'
$stage = Join-Path $artifacts "HeavierByTheKill-$Version"
$zip = Join-Path $artifacts "HeavierByTheKill-$Version-win-x64.zip"
$checksum = "$zip.sha256"

foreach ($path in @($publish,$stage,$zip,$checksum)) {
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifacts)
    $resolvedPath = [System.IO.Path]::GetFullPath($path)
    if (-not $resolvedPath.StartsWith($resolvedArtifacts + [System.IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the artifacts directory: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) { Remove-Item -LiteralPath $resolvedPath -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $publish,$stage,(Join-Path $stage 'payload') | Out-Null

Push-Location -LiteralPath $PSScriptRoot
try {
    cargo fmt --check
    if ($LASTEXITCODE -ne 0) { throw 'cargo fmt failed.' }
    cargo test --all-targets
    if ($LASTEXITCODE -ne 0) { throw 'cargo test failed.' }
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw 'cargo build failed.' }
    dotnet publish controller\HeavierByTheKill.Controller.csproj `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
finally { Pop-Location }

$payloadSources = [ordered]@{
    'HeavierByTheKill.Controller.exe' = Join-Path $publish 'HeavierByTheKill.Controller.exe'
    'heavier_by_the_kill.dll' = Join-Path $PSScriptRoot 'target\release\heavier_by_the_kill.dll'
    'heavier_by_the_kill_input.dll' = Join-Path $PSScriptRoot 'target\release\heavier_by_the_kill_input.dll'
    'heavier_by_the_kill.ini' = Join-Path $PSScriptRoot 'heavier_by_the_kill.ini'
    'obs-overlay.html' = Join-Path $PSScriptRoot 'controller\obs-overlay.html'
}
foreach ($name in $payloadSources.Keys) {
    $source = $payloadSources[$name]
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Published payload is missing $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stage 'payload')
}

$releaseFiles = @(
    'INSTALL MOD.cmd','START MOD.cmd','UNINSTALL MOD.cmd',
    'install.ps1','run-mod.ps1','uninstall.ps1','steam-path.ps1',
    'start-heavier-by-the-kill.cmd','edit-heavier-by-the-kill-config.cmd',
    'README - START HERE.txt','README.md','OBS-SETUP.txt','LICENSE'
)
foreach ($name in $releaseFiles) {
    $source = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Release file is missing: $name" }
    Copy-Item -LiteralPath $source -Destination $stage
}

$forbidden = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object {
    $_.Extension -in @('.save','.pdb','.cs','.rs') -or $_.FullName -match '[\\/](profiles|target|bin|obj)[\\/]'
}
if ($forbidden) { throw "Forbidden release content: $($forbidden.FullName -join ', ')" }

Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksum -Value "$hash  $([System.IO.Path]::GetFileName($zip))" -Encoding ascii
Write-Host "Release created: $zip"
Write-Host "SHA-256: $hash"
