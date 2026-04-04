[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\transition_sweep.live.json",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\transition_sweep.live.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Transition sweep report not found: $SourcePath"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Copy-Item -LiteralPath $SourcePath -Destination $OutputPath -Force
Write-Host "Copied transition sweep report to $OutputPath"
