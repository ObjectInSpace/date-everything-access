[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navmesh_export.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\navmesh_export.live.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Navmesh export not found: $SourcePath"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Copy-Item -LiteralPath $SourcePath -Destination $OutputPath -Force
Write-Host "Copied navmesh export to $OutputPath"
