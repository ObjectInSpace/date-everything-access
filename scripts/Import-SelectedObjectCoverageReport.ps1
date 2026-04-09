[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\selected_object_coverage.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\selected_object_coverage.live.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Selected object coverage report not found: $SourcePath"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Copy-Item -LiteralPath $SourcePath -Destination $OutputPath -Force
Write-Host "Copied selected object coverage report to $OutputPath"
