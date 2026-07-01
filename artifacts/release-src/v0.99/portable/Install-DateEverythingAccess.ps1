param(
    [string]$GamePath
)

$ErrorActionPreference = "Stop"

function Read-GamePath {
    param([string]$CurrentValue)

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        return $CurrentValue
    }

    Write-Host "Enter the full path to your Date Everything! game folder."
    return Read-Host
}

function Assert-GamePath {
    param([string]$CandidatePath)

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        throw "No game path was provided."
    }

    $resolvedPath = (Resolve-Path -LiteralPath $CandidatePath).Path
    $gameExePath = Join-Path $resolvedPath "Date Everything.exe"
    if (-not (Test-Path -LiteralPath $gameExePath)) {
        throw "Date Everything.exe was not found in $resolvedPath"
    }

    return $resolvedPath
}

$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$GamePath = Read-GamePath -CurrentValue $GamePath
$resolvedGamePath = Assert-GamePath -CandidatePath $GamePath

$itemsToCopy = @(
    ".doorstop_version",
    "doorstop_config.ini",
    "winhttp.dll",
    "Tolk.dll",
    "nvdaControllerClient64.dll",
    "BepInEx"
)

foreach ($item in $itemsToCopy) {
    $sourcePath = Join-Path $packageRoot $item
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required package file not found: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $resolvedGamePath $item) -Recurse -Force
}

Write-Host ""
Write-Host "Installation complete."
Write-Host "Installed Date Everything Access into:"
Write-Host $resolvedGamePath
