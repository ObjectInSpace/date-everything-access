# Prints the Date Everything game folder (the one with "Date Everything.exe") and exits 0,
# or exits 1 when it can't be found. Used by the .bat files in the project root.
# Order: the DATE_EVERYTHING_DIR environment variable, then every Steam library on this PC.
$ErrorActionPreference = "SilentlyContinue"

function Test-GameFolder([string]$p) {
    return -not [string]::IsNullOrWhiteSpace($p) -and (Test-Path -LiteralPath (Join-Path $p "Date Everything.exe"))
}

if (Test-GameFolder $env:DATE_EVERYTHING_DIR) {
    Write-Output $env:DATE_EVERYTHING_DIR
    exit 0
}

$roots = New-Object System.Collections.Generic.List[string]
foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam")) {
    $v = Get-ItemProperty -Path $key
    foreach ($name in @("SteamPath", "InstallPath")) {
        if ($v.$name) { $roots.Add(($v.$name -replace '/', '\')) }
    }
}
$roots.Add("C:\Program Files (x86)\Steam")

foreach ($r in $roots) {
    $libraries = New-Object System.Collections.Generic.List[string]
    $libraries.Add($r)
    $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
    if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $libraries.Add(($m.Groups[1].Value -replace '\\\\', '\'))
        }
    }
    foreach ($lib in $libraries) {
        $candidate = Join-Path $lib "steamapps\common\Date Everything"
        if (Test-GameFolder $candidate) {
            Write-Output $candidate
            exit 0
        }
    }
}
exit 1
