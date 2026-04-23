param(
    [string]$Configuration = "Debug",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\DateEverythingAccess.csproj"),
    [switch]$SkipNavigationLoopInspection,
    [switch]$RestorePackages
)

$ErrorActionPreference = "Stop"

$resolvedProject = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $resolvedProject

if (-not $SkipNavigationLoopInspection) {
    $inspectionScriptPath = Join-Path $PSScriptRoot "Inspect-NavigationLoopRisks.ps1"
    if (Test-Path -LiteralPath $inspectionScriptPath) {
        Write-Host "Running static navigation loop inspection..."
        & $inspectionScriptPath `
            -AccessibilityWatcherPath (Join-Path $projectDir "AccessibilityWatcher.cs") `
            -DoorNavigationPath (Join-Path $projectDir "AccessibilityWatcher.DoorNavigation.cs") `
            -FailOnRisk
    } else {
        Write-Warning "Navigation loop inspection script not found: $inspectionScriptPath"
    }
}

Write-Host "Building $resolvedProject ($Configuration)..."

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_HOME = Join-Path $projectDir ".dotnet"
if (-not (Test-Path -LiteralPath $env:DOTNET_CLI_HOME)) {
    New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME | Out-Null
}
if (-not $RestorePackages) {
    dotnet build $resolvedProject -c $Configuration --no-restore
} else {
    dotnet build $resolvedProject -c $Configuration
}
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit $LASTEXITCODE
}

$dllPath = Join-Path $projectDir "bin\$Configuration\net472\DateEverythingAccess.dll"
if (Test-Path -LiteralPath $dllPath) {
    Write-Host "Build succeeded."
    Write-Host "Output DLL: $dllPath"
} else {
    Write-Warning "Build finished but the expected DLL was not found: $dllPath"
}
