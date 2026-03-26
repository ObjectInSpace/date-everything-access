param(
    [string]$Configuration = "Release",
    [string]$ProjectPath = (Join-Path $PSScriptRoot "..\DateEverythingAccess.csproj"),
    [string]$GamePath = "D:\SteamLibrary\steamapps\Common\Date Everything"
)

$resolvedProject = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $resolvedProject
$mainSourcePath = Join-Path $projectDir "Main.cs"

if (-not (Test-Path -LiteralPath $mainSourcePath)) {
    Write-Error "Main.cs not found: $mainSourcePath"
    exit 1
}

$mainSource = Get-Content -LiteralPath $mainSourcePath -Raw
$versionMatch = [regex]::Match($mainSource, 'BepInPlugin\(".*?",\s*".*?",\s*"(?<version>[^"]+)"\)')
if (-not $versionMatch.Success) {
    Write-Error "Could not determine plugin version from Main.cs"
    exit 1
}

$version = $versionMatch.Groups["version"].Value
$releaseTag = "v$version"
$releaseSourceRoot = Join-Path $projectDir "artifacts\release-src\$releaseTag\portable"
$stagingRoot = Join-Path $projectDir "artifacts\release-staging"
$packageRoot = Join-Path $stagingRoot "DateEverythingAccess-portable-$releaseTag"
$zipPath = Join-Path $stagingRoot "DateEverythingAccess-portable-$releaseTag.zip"
$outputDllPath = Join-Path $projectDir "bin\$Configuration\net472\DateEverythingAccess.dll"

$requiredGameFiles = @(
    ".doorstop_version",
    "doorstop_config.ini",
    "winhttp.dll",
    "Tolk.dll",
    "nvdaControllerClient64.dll"
)

foreach ($requiredFile in $requiredGameFiles) {
    $requiredPath = Join-Path $GamePath $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        Write-Error "Required game file not found: $requiredPath"
        exit 1
    }
}

$coreSourcePath = Join-Path $GamePath "BepInEx\core"
if (-not (Test-Path -LiteralPath $coreSourcePath)) {
    Write-Error "BepInEx core directory not found: $coreSourcePath"
    exit 1
}

if (-not (Test-Path -LiteralPath $releaseSourceRoot)) {
    Write-Error "Release source folder not found: $releaseSourceRoot"
    exit 1
}

Write-Host "Building portable release for $releaseTag..."
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
dotnet build $resolvedProject -c $Configuration --no-restore -p:SkipCopyToPlugins=true
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $outputDllPath)) {
    Write-Error "Expected build output not found: $outputDllPath"
    exit 1
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path (Join-Path $packageRoot "BepInEx\core") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot "BepInEx\plugins") -Force | Out-Null

Copy-Item -LiteralPath $outputDllPath -Destination (Join-Path $packageRoot "BepInEx\plugins\DateEverythingAccess.dll") -Force
Copy-Item -LiteralPath $coreSourcePath -Destination (Join-Path $packageRoot "BepInEx") -Recurse -Force

foreach ($requiredFile in $requiredGameFiles) {
    Copy-Item -LiteralPath (Join-Path $GamePath $requiredFile) -Destination (Join-Path $packageRoot $requiredFile) -Force
}

Copy-Item -LiteralPath (Join-Path $releaseSourceRoot "README.txt") -Destination (Join-Path $packageRoot "README.txt") -Force
Copy-Item -LiteralPath (Join-Path $releaseSourceRoot "Install-DateEverythingAccess.ps1") -Destination (Join-Path $packageRoot "Install-DateEverythingAccess.ps1") -Force
Copy-Item -LiteralPath (Join-Path $releaseSourceRoot "THIRD-PARTY-NOTICES.txt") -Destination (Join-Path $packageRoot "THIRD-PARTY-NOTICES.txt") -Force

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Portable package created:"
Write-Host "  $zipPath"
