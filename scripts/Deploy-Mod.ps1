param(
    [string]$Configuration = "Debug",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\DateEverythingAccess.csproj"),
    # Game folder. Found automatically (DATE_EVERYTHING_DIR, then every Steam library) when omitted.
    [string]$GamePath,
    # Legacy no-op switch kept for backward compatibility with older calls.
    [switch]$SkipNavigationLoopInspection,
    [switch]$RestorePackages
)

$resolvedProject = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $resolvedProject
if (-not $GamePath) {
    $GamePath = & (Join-Path $PSScriptRoot "Find-GameDir.ps1")
    if (-not $GamePath) {
        Write-Error "Date Everything not found. Pass -GamePath or set DATE_EVERYTHING_DIR."
        exit 1
    }
}
$targetDir = Join-Path $GamePath "BepInEx\plugins"

if (-not (Test-Path -LiteralPath $targetDir)) {
    Write-Error "Target plugin directory not found: $targetDir"
    exit 1
}

& (Join-Path $PSScriptRoot "Build-Mod.ps1") `
    -Configuration $Configuration `
    -ProjectPath $resolvedProject `
    -RestorePackages:$RestorePackages `
    -GamePath $GamePath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$dllPath = Join-Path $projectDir "bin\$Configuration\net472\DateEverythingAccess.dll"
if (-not (Test-Path -LiteralPath $dllPath)) {
    Write-Error "Expected build output not found: $dllPath"
    exit 1
}

Copy-Item -LiteralPath $dllPath -Destination $targetDir -Force
Write-Host "Deployed DateEverythingAccess.dll to $targetDir"
