param(
    [string]$Configuration = "Debug",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\DateEverythingAccess.csproj"),
    [string]$GamePath = "D:\SteamLibrary\steamapps\Common\Date Everything"
)

$resolvedProject = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $resolvedProject
$targetDir = Join-Path $GamePath "BepInEx\plugins"

if (-not (Test-Path -LiteralPath $targetDir)) {
    Write-Error "Target plugin directory not found: $targetDir"
    exit 1
}

& (Join-Path $PSScriptRoot "Build-Mod.ps1") -Configuration $Configuration -ProjectPath $resolvedProject
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
