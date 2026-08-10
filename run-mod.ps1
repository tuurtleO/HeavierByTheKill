$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'steam-path.ps1')
if (-not (Get-Process -Name DarkSoulsRemastered -ErrorAction SilentlyContinue)) {
    throw 'Start Dark Souls Remastered in Offline mode and load a character first.'
}
$controller = Join-Path $PSScriptRoot 'HeavierByTheKill.Controller.exe'
if (-not (Test-Path -LiteralPath $controller)) {
    $gameDirectory = Resolve-DsrGameDirectory -AllowPrompt
    $controller = Join-Path $gameDirectory 'HeavierByTheKill\HeavierByTheKill.Controller.exe'
}
if (-not (Test-Path -LiteralPath $controller)) {
    throw 'Heavier by the Kill is not installed. Run install.ps1 first, or launch start-heavier-by-the-kill.cmd from the installed HeavierByTheKill folder.'
}
$controllerDirectory = Split-Path -Parent $controller
Push-Location -LiteralPath $controllerDirectory
try {
    & $controller run
}
finally {
    Pop-Location
}
