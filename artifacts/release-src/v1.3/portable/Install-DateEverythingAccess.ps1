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
    "prism.dll",
    "Prism-Licenses",
    "BepInEx"
)

foreach ($item in $itemsToCopy) {
    $sourcePath = Join-Path $packageRoot $item
    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required package file not found: $sourcePath"
    }

    $destinationPath = Join-Path $resolvedGamePath $item
    if (Test-Path -LiteralPath $sourcePath -PathType Container) {
        # Merge folder contents; copying the folder onto an existing one would nest it.
        New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
        Copy-Item -Path (Join-Path $sourcePath "*") -Destination $destinationPath -Recurse -Force
    } else {
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    }
}

Write-Host ""
Write-Host "Installation complete."
Write-Host "Installed Date Everything Access into:"
Write-Host $resolvedGamePath
