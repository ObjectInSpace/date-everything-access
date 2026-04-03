[CmdletBinding()]
param(
    [Parameter()]
    [string]$BundlePath = "D:\SteamLibrary\steamapps\Common\Date Everything\Date Everything_Data\StreamingAssets\aa\StandaloneWindows64\base_scene_thirdpersongreybox_scenes_all.bundle",

    [Parameter()]
    [string]$ExportedScenePath = "D:\Root\AssetRipper\1.3.12\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$ExportedSceneAssetFolder = "D:\Root\AssetRipper\1.3.12\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox",

    [Parameter()]
    [string]$SceneBundlesFolder = "D:\SteamLibrary\steamapps\Common\Date Everything\Date Everything_Data\StreamingAssets\aa\StandaloneWindows64",

    [Parameter()]
    [string]$OutputPath = ".\artifacts\navigation\thirdpersongreybox-navmesh-assets-report.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-AsciiTextFromBytes {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    return [System.Text.Encoding]::GetEncoding("iso-8859-1").GetString($Bytes)
}

function Test-Pattern {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    return [regex]::IsMatch($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Get-RegexMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Pattern
    )

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        [void]$set.Add($match.Value)
    }

    return @($set | Sort-Object)
}

if (-not (Test-Path -LiteralPath $BundlePath)) {
    throw "Scene bundle not found: $BundlePath"
}

if (-not (Test-Path -LiteralPath $ExportedScenePath)) {
    throw "Exported scene not found: $ExportedScenePath"
}

$exportedSceneText = Get-Content -LiteralPath $ExportedScenePath -Raw
$navMeshDataMatch = [regex]::Match($exportedSceneText, "m_NavMeshData:\s*\{fileID:\s*(-?\d+)\}")
$navMeshDataFileId = if ($navMeshDataMatch.Success) { [long]$navMeshDataMatch.Groups[1].Value } else { $null }

$bundleBytes = [System.IO.File]::ReadAllBytes($BundlePath)
$bundleText = Get-AsciiTextFromBytes -Bytes $bundleBytes

$bundleArchives = @(Get-RegexMatches -Text $bundleText -Pattern "CAB-[0-9a-f]{32}(?:\.sharedAssets(?:\.resS)?)?")
$bundleSceneNames = @(Get-RegexMatches -Text $bundleText -Pattern "ThirdPersonGreybox")
$sceneSidecarFiles = @()
$sceneNavMeshNamedFiles = @()
if (Test-Path -LiteralPath $ExportedSceneAssetFolder) {
    $sceneSidecarFiles = @(Get-ChildItem -LiteralPath $ExportedSceneAssetFolder -File | Select-Object -ExpandProperty Name)
    $sceneNavMeshNamedFiles = @(Get-ChildItem -LiteralPath $ExportedSceneAssetFolder -Recurse -File | Where-Object { $_.Name -match "NavMesh|navmesh|OffMesh" } | Select-Object -ExpandProperty FullName)
}

$sceneBundleReports = New-Object System.Collections.Generic.List[object]
if (Test-Path -LiteralPath $SceneBundlesFolder) {
    foreach ($bundleFile in @(Get-ChildItem -LiteralPath $SceneBundlesFolder -Filter "base_scene_*_scenes_all.bundle")) {
        $text = Get-AsciiTextFromBytes -Bytes ([System.IO.File]::ReadAllBytes($bundleFile.FullName))
        $sceneBundleReports.Add([pscustomobject]@{
            Name = $bundleFile.Name
            ContainsNavMeshSettings = Test-Pattern -Text $text -Pattern "NavMeshSettings"
            ContainsNavMeshDataTypeReference = Test-Pattern -Text $text -Pattern "PPtr<NavMeshData>|NavMeshData"
            ContainsNavMeshSurface = Test-Pattern -Text $text -Pattern "NavMeshSurface"
            ContainsNavMeshLink = Test-Pattern -Text $text -Pattern "NavMeshLink"
        })
    }
}

$conclusion = if ($null -eq $navMeshDataFileId) {
    "The exported scene did not expose an m_NavMeshData entry."
} elseif ($navMeshDataFileId -eq 0) {
    "The exported scene's NavMeshSettings references fileID 0, so there is no baked NavMeshData object linked from ThirdPersonGreybox."
} else {
    "The exported scene references a nonzero NavMeshData fileID and likely needs deeper bundle extraction."
}

$report = [ordered]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("o")
    BundlePath = $BundlePath
    ExportedScenePath = $ExportedScenePath
    ExportedSceneNavMeshDataFileId = $navMeshDataFileId
    ExportedSceneHasActiveNavMeshDataReference = ($null -ne $navMeshDataFileId -and $navMeshDataFileId -ne 0)
    ExportedSceneContainsNavMeshSettings = Test-Pattern -Text $exportedSceneText -Pattern "NavMeshSettings"
    ExportedSceneContainsNavMeshSurface = Test-Pattern -Text $exportedSceneText -Pattern "NavMeshSurface"
    ExportedSceneContainsNavMeshLink = Test-Pattern -Text $exportedSceneText -Pattern "NavMeshLink"
    ExportedSceneSidecarFiles = $sceneSidecarFiles
    ExportedSceneNavMeshNamedFiles = $sceneNavMeshNamedFiles
    BundleContainsNavMeshSettings = Test-Pattern -Text $bundleText -Pattern "NavMeshSettings"
    BundleContainsNavMeshDataTypeReference = Test-Pattern -Text $bundleText -Pattern "PPtr<NavMeshData>|NavMeshData"
    BundleContainsNavMeshSurface = Test-Pattern -Text $bundleText -Pattern "NavMeshSurface"
    BundleContainsNavMeshLink = Test-Pattern -Text $bundleText -Pattern "NavMeshLink"
    BundleContainsSharedAssetsReference = Test-Pattern -Text $bundleText -Pattern "\.sharedAssets"
    BundleContainsSharedAssetsResSReference = Test-Pattern -Text $bundleText -Pattern "\.sharedAssets\.resS"
    BundleReferencedArchives = $bundleArchives
    BundleSceneNames = $bundleSceneNames
    SceneBundleSummary = $sceneBundleReports.ToArray()
    Conclusion = $conclusion
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Wrote scene navmesh asset report to $OutputPath"
