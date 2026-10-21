# Date Everything Access test build installer. Runs from the self-extracting
# setup: finds the game, then copies the mod and its loader into it.
param([string]$GamePath)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$title = "Date Everything Access Setup"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# A hidden, always-on-top owner so every message box and the folder picker come to the
# front with keyboard focus (the setup runs without a window of its own).
$owner = New-Object System.Windows.Forms.Form
$owner.TopMost = $true
$owner.ShowInTaskbar = $false
$owner.StartPosition = "CenterScreen"
$owner.Size = New-Object System.Drawing.Size(1, 1)
$owner.Opacity = 0

function Say([string]$text, [string]$buttons = "OK", [string]$icon = "Information") {
    return [System.Windows.Forms.MessageBox]::Show($owner, $text, $title,
        [System.Windows.Forms.MessageBoxButtons]::$buttons,
        [System.Windows.Forms.MessageBoxIcon]::$icon)
}

function Test-GameFolder([string]$p) {
    return -not [string]::IsNullOrWhiteSpace($p) -and (Test-Path -LiteralPath (Join-Path $p "Date Everything.exe"))
}

function Find-Game {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
        try {
            $v = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @("SteamPath", "InstallPath")) {
                if ($v.$name) { $roots.Add(($v.$name -replace '/', '\')) }
            }
        } catch { }
    }
    $roots.Add("C:\Program Files (x86)\Steam")
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($r in $roots) {
        $libraries.Add($r)
        $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries.Add(($m.Groups[1].Value -replace '\\\\', '\'))
            }
        }
    }
    foreach ($lib in $libraries) {
        $candidate = Join-Path $lib "steamapps\common\Date Everything"
        if (Test-GameFolder $candidate) { return $candidate }
    }
    return $null
}

function Pick-Folder {
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Choose your Date Everything! game folder, the one that contains Date Everything.exe."
    $dlg.ShowNewFolderButton = $false
    if ($dlg.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
    return $null
}

try {
    if (-not (Test-GameFolder $GamePath)) {
        $GamePath = Find-Game
        if ($GamePath) {
            $answer = Say ("Found Date Everything! at:`n$GamePath`n`nInstall the mod there? Choose No to pick a different folder.") "YesNoCancel" "Question"
            if ($answer -eq "Cancel") { exit 1 }
            if ($answer -eq "No") { $GamePath = Pick-Folder }
        } else {
            Say "The installer could not find Date Everything! automatically. In the next window, choose your game folder, the one that contains Date Everything.exe." | Out-Null
            $GamePath = Pick-Folder
        }
    }
    if (-not $GamePath) { exit 1 }
    if (-not (Test-GameFolder $GamePath)) {
        Say "Date Everything.exe was not found in:`n$GamePath`n`nNothing was installed. Run the setup again and choose the game folder." "OK" "Error" | Out-Null
        exit 1
    }

    while (Get-Process -Name "Date Everything" -ErrorAction SilentlyContinue) {
        $answer = Say "Date Everything! is still running. Close the game completely, then choose OK to continue, or Cancel to stop." "OKCancel" "Warning"
        if ($answer -eq "Cancel") { exit 1 }
    }

    $staging = Join-Path $env:TEMP ("DateEverythingAccess-" + [guid]::NewGuid().ToString("N"))
    Expand-Archive -LiteralPath (Join-Path $here "payload.zip") -DestinationPath $staging -Force
    try {
        Get-ChildItem -LiteralPath $staging -Force | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $GamePath -Recurse -Force
        }
    } catch [System.UnauthorizedAccessException] {
        # The game folder needs administrator rights: ask once, and wait for it.
        $relaunchArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -GamePath `"$GamePath`""
        Start-Process -FilePath "powershell.exe" -ArgumentList $relaunchArgs -Verb RunAs -Wait
        exit 0
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    Say ("Date Everything Access is installed in:`n$GamePath`n`nStart the game and you should hear the mod's startup announcement. The README is in the game folder.") | Out-Null
    exit 0
} catch {
    Say ("Installation failed:`n" + $_.Exception.Message + "`n`nMake sure the game is closed and try again.") "OK" "Error" | Out-Null
    exit 1
}
