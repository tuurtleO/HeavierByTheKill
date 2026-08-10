function Test-DsrGameDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return Test-Path -LiteralPath (Join-Path $Path 'DarkSoulsRemastered.exe') -PathType Leaf
}

function Get-SteamRoots {
    $roots = [System.Collections.Generic.List[string]]::new()

    $running = Get-Process -Name 'DarkSoulsRemastered' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($running) {
        try { $roots.Add((Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $running.MainModule.FileName))))) } catch { }
    }

    foreach ($registryPath in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )) {
        try {
            $steam = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
            foreach ($property in @('SteamPath','InstallPath')) {
                $value = $steam.$property
                if (-not [string]::IsNullOrWhiteSpace($value)) { $roots.Add($value) }
            }
        } catch { }
    }

    foreach ($fallback in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
        (Join-Path $env:ProgramFiles 'Steam')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($fallback)) { $roots.Add($fallback) }
    }

    $expanded = [System.Collections.Generic.List[string]]::new()
    foreach ($root in @($roots)) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $expanded.Add($root)
        $libraryFile = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFile)) { continue }
        try {
            $text = Get-Content -LiteralPath $libraryFile -Raw
            foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $expanded.Add(($match.Groups[1].Value -replace '\\\\','\'))
            }
        } catch { }
    }

    return @($expanded | ForEach-Object {
        try { [System.IO.Path]::GetFullPath($_) } catch { }
    } | Where-Object { $_ } | Select-Object -Unique)
}

function Resolve-DsrGameDirectory {
    param(
        [string]$RequestedPath,
        [switch]$AllowPrompt
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (Test-DsrGameDirectory $resolved) { return $resolved }
        throw "DarkSoulsRemastered.exe was not found in $resolved"
    }

    $candidates = foreach ($root in Get-SteamRoots) {
        if (Test-DsrGameDirectory $root) { $root }
        $common = Join-Path $root 'steamapps\common\DARK SOULS REMASTERED'
        if (Test-DsrGameDirectory $common) { $common }
    }
    $candidate = @($candidates | Select-Object -Unique | Select-Object -First 1)
    if ($candidate.Count -eq 1) { return [System.IO.Path]::GetFullPath($candidate[0]) }

    if ($AllowPrompt) {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Select your DARK SOULS REMASTERED game folder'
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $resolved = [System.IO.Path]::GetFullPath($dialog.SelectedPath)
            if (Test-DsrGameDirectory $resolved) { return $resolved }
            throw "DarkSoulsRemastered.exe was not found in $resolved"
        }
    }

    throw 'Dark Souls Remastered was not found. Install it through Steam, or run this script with -GameDir pointing to the game folder.'
}
