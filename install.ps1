param(
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'steam-path.ps1')
$GameDir = Resolve-DsrGameDirectory -RequestedPath $GameDir -AllowPrompt
$expectedHash = 'A45AAA36DD2F6CC151670A639EA5547043CF38EA79FF4178B963C6ED71F98D7B'
$gameExe = Join-Path $GameDir 'DarkSoulsRemastered.exe'
if (-not (Test-Path -LiteralPath $gameExe)) { throw "DarkSoulsRemastered.exe was not found in $GameDir" }
$actualHash = (Get-FileHash -LiteralPath $gameExe -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw "Unsupported game executable ($actualHash). Expected app 1.03.1." }

$packagedSource = Join-Path $PSScriptRoot 'payload'
$publishedSource = Join-Path $PSScriptRoot 'controller\bin\Release\net10.0-windows\win-x64\publish'
$buildSource = Join-Path $PSScriptRoot 'controller\bin\Release\net10.0-windows'
$source = @($packagedSource,$publishedSource,$buildSource) |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'HeavierByTheKill.Controller.exe') } |
    Select-Object -First 1
if (-not $source) {
    throw 'Release payload not found. Download the release ZIP, or build the release controller first.'
}
$destination = Join-Path $GameDir 'HeavierByTheKill'
$legacyDestination = Join-Path $GameDir 'HeavierByKill'
$installMarker = Join-Path $destination '.installed-by-heavier-by-the-kill'
if ((Test-Path -LiteralPath $destination) -and -not (Test-Path -LiteralPath $installMarker)) {
    throw "Refusing to overwrite an unrecognized directory: $destination"
}
if ((Test-Path -LiteralPath $legacyDestination) -and -not (Test-Path -LiteralPath $destination)) {
    $legacyMarker = Join-Path $legacyDestination '.installed-by-heavier-by-kill'
    if (-not (Test-Path -LiteralPath $legacyMarker)) {
        throw "Refusing to migrate unrecognized directory: $legacyDestination"
    }
    Move-Item -LiteralPath $legacyDestination -Destination $destination
    Write-Host "Migrated the previous installation to $destination"
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$legacyConfig = Join-Path $destination 'heavier_by_kill.ini'
$installedConfig = Join-Path $destination 'heavier_by_the_kill.ini'
if ((Test-Path -LiteralPath $legacyConfig) -and -not (Test-Path -LiteralPath $installedConfig)) {
    Move-Item -LiteralPath $legacyConfig -Destination $installedConfig
}
$legacySave = Join-Path $destination 'heavier_by_kill.save'
$installedSave = Join-Path $destination 'heavier_by_the_kill.save'
if ((Test-Path -LiteralPath $legacySave) -and -not (Test-Path -LiteralPath $installedSave)) {
    Move-Item -LiteralPath $legacySave -Destination $installedSave
}
$profilesDirectory = Join-Path $destination 'profiles'
New-Item -ItemType Directory -Force -Path $profilesDirectory | Out-Null
$globalProgressBackup = Join-Path $destination 'heavier_by_the_kill.global-before-profiles.save'
if ((Test-Path -LiteralPath $installedSave) -and -not (Test-Path -LiteralPath $globalProgressBackup)) {
    Copy-Item -LiteralPath $installedSave -Destination $globalProgressBackup
}
foreach ($file in Get-ChildItem -LiteralPath $source -File) {
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}
foreach ($optionalManagedFile in @(
    'HeavierByTheKill.Controller.dll',
    'HeavierByTheKill.Controller.runtimeconfig.json',
    'HeavierByTheKill.Controller.deps.json'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $optionalManagedFile))) {
        $stale = Join-Path $destination $optionalManagedFile
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
    }
}
$sourceConfig = Join-Path $source 'heavier_by_the_kill.ini'
if (-not (Test-Path -LiteralPath $installedConfig)) {
    Copy-Item -LiteralPath $sourceConfig -Destination $destination
}
else {
    # Refresh the comments and setting list while retaining every customized
    # value. Values that exactly match an obsolete default receive the new,
    # gentler balance; genuinely customized values are never replaced.
    $existingValues = @{}
    foreach ($line in Get-Content -LiteralPath $installedConfig) {
        if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*([^#]+?)\s*$') {
            $existingValues[$Matches[1]] = $Matches[2].Trim()
        }
    }
    $oldDefaults = @{
        speed_loss_per_weight = '0.016'
        stamina_per_weight = '0.028'
        recovery_per_weight = '0.022'
    }
    $templateKeys = @{}
    $mergedConfig = foreach ($line in Get-Content -LiteralPath $sourceConfig) {
        if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*([^#]+?)\s*$') {
            $key = $Matches[1]
            $templateValue = $Matches[2].Trim()
            $templateKeys[$key] = $true
            if ($existingValues.ContainsKey($key)) {
                $value = $existingValues[$key]
                if ($oldDefaults.ContainsKey($key) -and $value -eq $oldDefaults[$key]) { $value = $templateValue }
                "$key=$value"
            }
            else { $line }
        }
        else { $line }
    }
    $unknownKeys = @($existingValues.Keys | Where-Object { -not $templateKeys.ContainsKey($_) } | Sort-Object)
    if ($unknownKeys.Count -gt 0) {
        $mergedConfig += ''
        $mergedConfig += '# Unrecognized settings preserved from the previous version'
        foreach ($key in $unknownKeys) { $mergedConfig += "$key=$($existingValues[$key])" }
    }
    Set-Content -LiteralPath $installedConfig -Value $mergedConfig -Encoding UTF8
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-mod.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'steam-path.ps1') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'start-heavier-by-the-kill.cmd') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'edit-heavier-by-the-kill-config.cmd') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'OBS-SETUP.txt') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $destination 'uninstall-mod.ps1') -Force
Set-Content -LiteralPath $installMarker -Value $expectedHash
$obsolete = @(
    'HeavierByKill.Controller.dll','HeavierByKill.Controller.exe','HeavierByKill.Controller.runtimeconfig.json',
    'HeavierByKill.Controller.deps.json','heavier_by_kill.dll','start-heavier-by-kill.cmd','.installed-by-heavier-by-kill'
)
foreach ($name in $obsolete) {
    $path = Join-Path $destination $name
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}
Write-Host "Installed to $destination. Start DSR offline, load a character, then double-click HeavierByTheKill\start-heavier-by-the-kill.cmd."
