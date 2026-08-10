param(
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'steam-path.ps1')
$GameDir = Resolve-DsrGameDirectory -RequestedPath $GameDir -AllowPrompt
$destination = [System.IO.Path]::GetFullPath((Join-Path $GameDir 'HeavierByTheKill'))
$marker = Join-Path $destination '.installed-by-heavier-by-the-kill'
if (-not (Test-Path -LiteralPath $marker)) { throw "Refusing to remove unrecognized directory: $destination" }
if ([System.IO.Path]::GetFileName($destination) -ne 'HeavierByTheKill') { throw "Unexpected install path: $destination" }
$gameExe = Join-Path ([System.IO.Directory]::GetParent($destination).FullName) 'DarkSoulsRemastered.exe'
if (-not (Test-Path -LiteralPath $gameExe)) { throw "Refusing to remove a directory outside a DSR installation: $destination" }
$save = Join-Path $destination 'heavier_by_the_kill.save'
if (Test-Path -LiteralPath $save) {
    $backup = Join-Path $GameDir 'heavier_by_the_kill.save.backup'
    Copy-Item -LiteralPath $save -Destination $backup -Force
    Write-Host "Progress backed up to $backup"
}
$profiles = Join-Path $destination 'profiles'
if ((Test-Path -LiteralPath $profiles) -and @(Get-ChildItem -LiteralPath $profiles -File).Count -gt 0) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $profilesBackup = Join-Path $GameDir "heavier_by_the_kill.profiles.backup-$stamp"
    Copy-Item -LiteralPath $profiles -Destination $profilesBackup -Recurse
    Write-Host "Character profiles backed up to $profilesBackup"
}
$controllers = @(Get-Process -Name 'HeavierByTheKill.Controller' -ErrorAction SilentlyContinue)
foreach ($controller in $controllers) {
    $controllerPath = $null
    try { $controllerPath = $controller.MainModule.FileName } catch { }
    if ($controllerPath -and [System.IO.Path]::GetDirectoryName($controllerPath) -eq $destination) {
        Stop-Process -Id $controller.Id -Force
        $controller.WaitForExit(2000) | Out-Null
    }
}
# The input bridge observes controller termination and unloads from DSR.
Start-Sleep -Milliseconds 100
Set-Location -LiteralPath $GameDir
Remove-Item -LiteralPath $destination -Recurse -Force
Write-Host 'Heavier by the Kill removed. No original game files required restoration.'
