[CmdletBinding()]
param(
    [Parameter()]
    [switch]$ImportRuntimeSweeps,

    [Parameter()]
    [switch]$ImportOpenSweep,

    [Parameter()]
    [switch]$ImportDoorSweep,

    [Parameter()]
    [switch]$RegenerateStaticArtifacts,

    [Parameter()]
    [switch]$RegenerateSceneData,

    [Parameter()]
    [switch]$InspectSceneNavMeshAssets,

    [Parameter()]
    [switch]$SkipSemanticAudit,

    [Parameter()]
    [string]$ScenePath = "D:\root\AssetRipper\1.3.12\extracted\Ripped\ExportedProject\Assets\ThirdPersonGreybox.unity",

    [Parameter()]
    [string]$PluginGraphPath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\navigation_graph.json",

    [Parameter()]
    [string]$SceneNavigationDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$SceneBlockerDataPath = ".\artifacts\navigation\thirdpersongreybox-blockers.json",

    [Parameter()]
    [string]$GeneratedGraphPath = ".\artifacts\navigation\navigation_graph.generated.json",

    [Parameter()]
    [string]$GeneratedLocalMapPath = ".\artifacts\navigation\local_navigation_maps.generated.json",

    [Parameter()]
    [string]$StaticValidationPath = ".\artifacts\navigation\transition_validation.static.json",

    [Parameter()]
    [string]$DoorAuditPath = ".\artifacts\navigation\door_transition_audit.json",

    [Parameter()]
    [string]$DoorAuditSummaryPath = ".\artifacts\navigation\door_transition_audit.summary.txt",

    [Parameter()]
    [string]$OpenSweepSourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\transition_sweep.live.json",

    [Parameter()]
    [string]$OpenSweepOutputPath = ".\artifacts\navigation\transition_sweep.live.json",

    [Parameter()]
    [string]$OpenSweepSummaryPath = ".\artifacts\navigation\transition_sweep.summary.txt",

    [Parameter()]
    [string]$DoorSweepSourcePath = "D:\SteamLibrary\steamapps\Common\Date Everything\BepInEx\plugins\door_transition_sweep.live.json",

    [Parameter()]
    [string]$DoorSweepOutputPath = ".\artifacts\navigation\door_transition_sweep.live.json",

    [Parameter()]
    [string]$DoorSweepSummaryPath = ".\artifacts\navigation\door_transition_sweep.summary.txt",

    [Parameter()]
    [string]$SemanticOutputPath = ".\artifacts\navigation\transition_semantics.generated.json",

    [Parameter()]
    [string]$SemanticSummaryPath = ".\artifacts\navigation\transition_semantics.summary.txt"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$completedSteps = New-Object System.Collections.Generic.List[string]

function Invoke-NavigationStep {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [scriptblock]$Script
    )

    Write-Host "== $Name =="
    & $Script
    $script:completedSteps.Add($Name)
}

function Assert-ArtifactExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

if ($ImportRuntimeSweeps) {
    $ImportOpenSweep = $true
    $ImportDoorSweep = $true
}

if ($RegenerateSceneData) {
    $RegenerateStaticArtifacts = $true
}

if ($RegenerateSceneData) {
    Invoke-NavigationStep -Name "Export scene navigation data" -Script {
        & (Join-Path $PSScriptRoot "Export-SceneNavigationData.ps1") `
            -ScenePath $ScenePath `
            -GraphPath $PluginGraphPath `
            -OutputPath $SceneNavigationDataPath
    }

    Invoke-NavigationStep -Name "Export scene blocker data" -Script {
        & (Join-Path $PSScriptRoot "Export-SceneBlockerData.ps1") `
            -ScenePath $ScenePath `
            -OutputPath $SceneBlockerDataPath
    }
}

if ($InspectSceneNavMeshAssets) {
    Invoke-NavigationStep -Name "Inspect scene navmesh assets" -Script {
        & (Join-Path $PSScriptRoot "Inspect-SceneNavMeshAssets.ps1")
    }
}

if ($RegenerateStaticArtifacts) {
    Assert-ArtifactExists -Path $SceneNavigationDataPath -Description "Scene navigation data"
    Assert-ArtifactExists -Path $SceneBlockerDataPath -Description "Scene blocker data"
    Assert-ArtifactExists -Path $PluginGraphPath -Description "Plugin navigation graph"

    Invoke-NavigationStep -Name "Build generated navigation graph" -Script {
        & (Join-Path $PSScriptRoot "Build-NavigationGraph.ps1") `
            -SceneDataPath $SceneNavigationDataPath `
            -InputGraphPath $PluginGraphPath `
            -OutputPath $GeneratedGraphPath
    }

    Invoke-NavigationStep -Name "Build local navigation maps" -Script {
        & (Join-Path $PSScriptRoot "Build-LocalNavigationMaps.ps1") `
            -NavigationDataPath $SceneNavigationDataPath `
            -BlockerDataPath $SceneBlockerDataPath `
            -GraphPath $PluginGraphPath `
            -OutputPath $GeneratedLocalMapPath
    }

    Invoke-NavigationStep -Name "Inspect generated transition metadata" -Script {
        & (Join-Path $PSScriptRoot "Inspect-NavigationTransitions.ps1") `
            -InputPath $GeneratedGraphPath `
            -OutputPath $StaticValidationPath
    }

    Invoke-NavigationStep -Name "Inspect door transition metadata" -Script {
        & (Join-Path $PSScriptRoot "Inspect-DoorTransitionMetadata.ps1") `
            -GraphPath $GeneratedGraphPath `
            -SceneDataPath $SceneNavigationDataPath `
            -OutputPath $DoorAuditPath `
            -SummaryPath $DoorAuditSummaryPath
    }
}

if ($ImportOpenSweep) {
    Invoke-NavigationStep -Name "Import open-passage sweep" -Script {
        & (Join-Path $PSScriptRoot "Import-TransitionSweepReport.ps1") `
            -SourcePath $OpenSweepSourcePath `
            -OutputPath $OpenSweepOutputPath `
            -SummaryPath $OpenSweepSummaryPath
    }
}

if ($ImportDoorSweep) {
    Invoke-NavigationStep -Name "Import door sweep" -Script {
        & (Join-Path $PSScriptRoot "Import-DoorTransitionSweepReport.ps1") `
            -SourcePath $DoorSweepSourcePath `
            -OutputPath $DoorSweepOutputPath `
            -SummaryPath $DoorSweepSummaryPath
    }
}

if (-not $SkipSemanticAudit) {
    Assert-ArtifactExists -Path $GeneratedGraphPath -Description "Generated navigation graph"
    Assert-ArtifactExists -Path $GeneratedLocalMapPath -Description "Generated local navigation maps"

    Invoke-NavigationStep -Name "Generate transition semantic audit" -Script {
        & (Join-Path $PSScriptRoot "Inspect-TransitionSemantics.ps1") `
            -GraphPath $GeneratedGraphPath `
            -LocalMapPath $GeneratedLocalMapPath `
            -StaticValidationPath $StaticValidationPath `
            -DoorAuditPath $DoorAuditPath `
            -OpenSweepPath $OpenSweepOutputPath `
            -DoorSweepPath $DoorSweepOutputPath `
            -OutputPath $SemanticOutputPath `
            -SummaryPath $SemanticSummaryPath
    }
}

if ($completedSteps.Count -eq 0) {
    Write-Host "No artifact steps ran. Use -ImportRuntimeSweeps, -RegenerateStaticArtifacts, or omit -SkipSemanticAudit."
} else {
    Write-Host "Navigation artifact update complete."
    Write-Host "Completed steps:"
    foreach ($step in $completedSteps) {
        Write-Host "- $step"
    }
}
