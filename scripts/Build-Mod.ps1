param(
    [string]$Configuration = "Debug",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\DateEverythingAccess.csproj")
)

$resolvedProject = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $resolvedProject

Write-Host "Building $resolvedProject ($Configuration)..."

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet build $resolvedProject -c $Configuration
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
