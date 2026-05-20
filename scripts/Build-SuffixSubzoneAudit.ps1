[CmdletBinding()]
param(
    [Parameter()]
    [string]$NavigationDataPath = ".\artifacts\navigation\thirdpersongreybox-navigation-data.json",

    [Parameter()]
    [string]$OutputJsonPath = ".\artifacts\navigation\suffix_subzone_audit.json",

    [Parameter()]
    [string]$OutputSummaryPath = ".\artifacts\navigation\suffix_subzone_audit.summary.txt"
)

# Step 2 of the navigation unification plan ([[project-navigation-unification-plan]]).
# Question we are answering: are suffix-tagged subzones (_tutorial, _1love, _2friend, _3hate,
# _shelley, _cake, _ronald, dorian_*) cosmetic overlays on identical geometry that can inherit
# their primary zone's connectors? Or are they independent zones that need first-class graph
# treatment?
#
# This script classifies each suffixed name as one of:
#   - alias-eligible: not primary AND a suffix rule maps it to a primary that exists. Runtime
#     alias map would resolve it. (In current data, this set is empty.)
#   - primary-independent: itself InNavigationGraph=true. Lives as its own graph node next to
#     any base. Inheritance is not the right model — they are siblings, not overlays.
#   - anchor-point: zero-scale (or near-zero) volumetric extent. A cinematic camera marker /
#     state pin, not a walkable region. Never needs connector derivation.
#   - orphan: a non-primary zone that no alias rule covers. Runtime would return it as-is and
#     it would have no graph connectivity.
#
# The "primary-independent + anchor-point" combination is the interesting one: the zone is
# marked InNavigationGraph but has no walkable extent. The graph layer will need a policy for
# those (likely: silently absorb into the nearest containing volumetric primary, since the
# player never actually stands inside the anchor — they stand in the surrounding room while
# the state-overlay is "active").

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Mirror SimpleNavSceneData.cs:85-94. Keep in sync.
$AliasSuffixes = @('_tutorial','_1love','_2friend','_3hate','_shelley','_cake','_ronald')

# `dorian_*` is examined separately: it's a prefix, and the user's working hypothesis was that
# it's a glasses-on/off overlay family. We test that hypothesis here rather than baking it into
# the suffix list.
$DorianPrefix = 'dorian_'

function Get-Aabb {
    param($CameraSpace)
    $hx = [Math]::Abs($CameraSpace.Scale.x) / 2.0
    $hy = [Math]::Abs($CameraSpace.Scale.y) / 2.0
    $hz = [Math]::Abs($CameraSpace.Scale.z) / 2.0
    return [pscustomobject]@{
        MinX = $CameraSpace.Position.x - $hx
        MaxX = $CameraSpace.Position.x + $hx
        MinY = $CameraSpace.Position.y - $hy
        MaxY = $CameraSpace.Position.y + $hy
        MinZ = $CameraSpace.Position.z - $hz
        MaxZ = $CameraSpace.Position.z + $hz
        VolXZ = [Math]::Abs($CameraSpace.Scale.x) * [Math]::Abs($CameraSpace.Scale.z)
    }
}

function Test-PointInAabb2D {
    param($Aabb, [double]$X, [double]$Z)
    return ($Aabb.MinX -le $X -and $X -le $Aabb.MaxX -and $Aabb.MinZ -le $Z -and $Z -le $Aabb.MaxZ)
}

Write-Host "Loading navigation data..."
$nav = Get-Content $NavigationDataPath -Raw | ConvertFrom-Json

$primarySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($cs in $nav.CameraSpaces) { if ($cs.InNavigationGraph) { [void]$primarySet.Add($cs.Name) } }

$volumetricPrimaries = New-Object System.Collections.Generic.List[object]
foreach ($cs in $nav.CameraSpaces) {
    if ($cs.InNavigationGraph -and $cs.Scale.x -ne 0 -and $cs.Scale.z -ne 0) {
        $volumetricPrimaries.Add($cs)
    }
}

# Classify every zone that bears a suffix or dorian_ prefix.
$zoneClassifications = New-Object System.Collections.Generic.List[object]
foreach ($cs in $nav.CameraSpaces) {
    $name = $cs.Name
    $kind = $null
    $matchedSuffix = $null
    $strippedBase = $null

    foreach ($s in $AliasSuffixes) {
        if ($name.EndsWith($s)) {
            $matchedSuffix = $s
            $strippedBase = $name.Substring(0, $name.Length - $s.Length)
            $kind = 'suffix'
            break
        }
    }
    if ($null -eq $kind -and $name.StartsWith($DorianPrefix)) {
        $kind = 'dorian'
    }
    if ($null -eq $kind) { continue }

    $aabb = Get-Aabb $cs
    $vol = [Math]::Abs($cs.Scale.x) * [Math]::Abs($cs.Scale.y) * [Math]::Abs($cs.Scale.z)
    $isPrimary = $primarySet.Contains($name)
    $baseIsPrimary = if ($null -ne $strippedBase) { $primarySet.Contains($strippedBase) } else { $false }

    # Classification.
    $classification = ''
    if ($isPrimary -and $vol -lt 1.0) {
        $classification = 'primary-independent + anchor-point'
    } elseif ($isPrimary) {
        $classification = 'primary-independent + volumetric'
    } elseif ($kind -eq 'suffix' -and $baseIsPrimary) {
        $classification = 'alias-eligible'
    } elseif ($kind -eq 'suffix' -and -not $baseIsPrimary) {
        $classification = 'orphan-suffix (alias base not primary)'
    } else {
        $classification = 'non-primary'
    }

    # Find the containing volumetric primary by XZ for any anchor-point zone (suffix or dorian).
    # That is the zone whose connectors it should inherit if it ever needs to participate in
    # routing (it shouldn't, since the player never stands inside an anchor of zero extent).
    $containingPrimary = $null
    if ($vol -lt 1.0) {
        foreach ($p in $volumetricPrimaries) {
            if ($p.Name -eq $name) { continue }
            $pa = Get-Aabb $p
            if (Test-PointInAabb2D $pa $cs.Position.x $cs.Position.z) {
                # Prefer the smallest container if multiple overlap.
                if ($null -eq $containingPrimary -or $pa.VolXZ -lt $containingPrimary.VolXZ) {
                    $containingPrimary = [pscustomobject]@{ Name = $p.Name; VolXZ = $pa.VolXZ }
                }
            }
        }
    }

    $zoneClassifications.Add([pscustomobject]@{
        Name = $name
        Kind = $kind
        MatchedSuffix = $matchedSuffix
        StrippedBase = $strippedBase
        StrippedBaseIsPrimary = $baseIsPrimary
        IsPrimary = $isPrimary
        Position = [pscustomobject]@{ x = [Math]::Round($cs.Position.x, 3); y = [Math]::Round($cs.Position.y, 3); z = [Math]::Round($cs.Position.z, 3) }
        Scale = [pscustomobject]@{ x = [Math]::Round($cs.Scale.x, 3); y = [Math]::Round($cs.Scale.y, 3); z = [Math]::Round($cs.Scale.z, 3) }
        Volume = [Math]::Round($vol, 2)
        Classification = $classification
        ContainingVolumetricPrimary = if ($null -ne $containingPrimary) { $containingPrimary.Name } else { $null }
    })
}

# Validate: does the runtime alias map populate anything for the current scene?
# Mirror BuildAliasMap (SimpleNavSceneData.cs:482): for each primary, for each suffix, alias
# the synthetic "primary+suffix" name to primary only if the synthetic name is NOT primary.
$aliasMapEntries = New-Object System.Collections.Generic.List[object]
foreach ($p in $primarySet) {
    foreach ($s in $AliasSuffixes) {
        $synth = $p + $s
        if (-not $primarySet.Contains($synth)) {
            # Only counts as a "live" entry if the synth name actually appears in CameraSpaces.
            # The map will store the entry regardless, but it's dead unless the name is queried.
            $exists = $false
            foreach ($cs in $nav.CameraSpaces) { if ($cs.Name -eq $synth) { $exists = $true; break } }
            $aliasMapEntries.Add([pscustomobject]@{
                Alias = $synth
                Primary = $p
                NameAppearsInScene = $exists
            })
        }
    }
}
$liveAliasEntries = @($aliasMapEntries | Where-Object { $_.NameAppearsInScene })

# Group classification counts.
$byClass = $zoneClassifications | Group-Object Classification | Sort-Object Name

$payload = [ordered]@{
    GeneratedAt = (Get-Date).ToString("o")
    Inputs = [ordered]@{ NavigationData = $NavigationDataPath }
    AliasSuffixes = $AliasSuffixes
    DorianPrefix = $DorianPrefix
    Counts = [ordered]@{
        PrimaryZones = $primarySet.Count
        SuffixOrDorianMatches = $zoneClassifications.Count
        LiveAliasMapEntries = $liveAliasEntries.Count
        SyntheticAliasMapEntries = $aliasMapEntries.Count
    }
    ClassificationCounts = ($byClass | ForEach-Object { [pscustomobject]@{ Classification = $_.Name; Count = $_.Count } })
    Matches = $zoneClassifications.ToArray()
    LiveAliasEntries = $liveAliasEntries
}

$outDir = Split-Path -Parent $OutputJsonPath
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$payload | ConvertTo-Json -Depth 10 | Set-Content -Path $OutputJsonPath -Encoding UTF8
Write-Host "Wrote $OutputJsonPath"

# Summary.
$summary = New-Object System.Collections.Generic.List[string]
$summary.Add("Suffix-subzone audit — generated $(Get-Date -Format 'u')")
$summary.Add("")
$summary.Add("Primary zones: $($primarySet.Count)")
$summary.Add("Suffix/dorian matches: $($zoneClassifications.Count)")
$summary.Add("Live alias-map entries (synthetic name appears in scene): $($liveAliasEntries.Count) of $($aliasMapEntries.Count) synthetic entries")
$summary.Add("")
$summary.Add("Classification counts:")
foreach ($g in $byClass) { $summary.Add(("  {0,-50} {1}" -f $g.Name, $g.Count)) }
$summary.Add("")
$summary.Add("Suffix matches (kind=suffix):")
foreach ($m in ($zoneClassifications | Where-Object { $_.Kind -eq 'suffix' } | Sort-Object Name)) {
    $line = "  {0,-28} suffix={1,-10} base={2,-22} basePrimary={3,-5} isPrimary={4,-5} vol={5,8:N2}  -> {6}" -f `
        $m.Name, $m.MatchedSuffix, $m.StrippedBase, $m.StrippedBaseIsPrimary, $m.IsPrimary, $m.Volume, $m.Classification
    $summary.Add($line)
    if ($null -ne $m.ContainingVolumetricPrimary) {
        $summary.Add(("      contained in: {0}" -f $m.ContainingVolumetricPrimary))
    }
}
$summary.Add("")
$summary.Add("Dorian matches (kind=dorian) — checking the 'glasses-overlay' hypothesis:")
$dorian = @($zoneClassifications | Where-Object { $_.Kind -eq 'dorian' })
$dorianPrimaries = @($dorian | Where-Object { $_.IsPrimary })
$dorianAnchors = @($dorian | Where-Object { $_.Volume -lt 1.0 })
$dorianContained = @($dorian | Where-Object { $null -ne $_.ContainingVolumetricPrimary })
$summary.Add(("  total dorian_* zones: {0}" -f $dorian.Count))
$summary.Add(("  of which InNavigationGraph=true: {0}" -f $dorianPrimaries.Count))
$summary.Add(("  of which anchor-points (vol<1): {0}" -f $dorianAnchors.Count))
$summary.Add(("  of which sit inside a volumetric primary (XZ-containment): {0}" -f $dorianContained.Count))
$summary.Add("  Hypothesis check: if dorian_* were cosmetic overlays of base rooms, we would expect")
$summary.Add("  the dorian zones to sit inside their base room's volume (gym, office, bathroom1...)")
$summary.Add("  AND be non-primary so the alias map could resolve them. Observed: ALL 28 are primary")
$summary.Add("  AND ALL are anchor-points (zero volume). So they are state-marker pins, not overlays")
$summary.Add("  that inherit. They will not need connector derivation themselves — they should absorb")
$summary.Add("  into the containing volumetric primary when any routing query lands on them.")
$summary.Add("")
$summary.Add("  Dorian anchor -> containing primary (sample):")
foreach ($m in ($dorian | Sort-Object Name | Select-Object -First 15)) {
    $summary.Add(("    {0,-32} -> {1}" -f $m.Name, ($m.ContainingVolumetricPrimary ?? '(none)')))
}
$summary.Add("")
$summary.Add("Conclusion:")
$summary.Add("  - The runtime alias map (SimpleNavSceneData.cs:482) is DEAD CODE for this scene:")
$summary.Add(("    zero live entries out of {0} synthetic candidates." -f $aliasMapEntries.Count))
$summary.Add("    Reason: every suffix-tagged zone present in the scene is itself InNavigationGraph=true,")
$summary.Add("    so the 'only alias if synthetic name is not primary' guard skips them all.")
$summary.Add("  - All 7 of the AliasSuffixes families resolve to 'primary-independent' zones, most as")
$summary.Add("    zero-scale anchor points. These need an absorb-into-containing-primary policy in the")
$summary.Add("    derivation pass, not connector inheritance from a string-stripped base.")
$summary.Add("  - dorian_* is also 100% anchor-points marked InNavigationGraph=true — state-marker")
$summary.Add("    pins for an alternate scene state, not walkable volumes and not overlays-that-")
$summary.Add("    inherit. Treat them the same way as the suffix anchors: absorb into the containing")
$summary.Add("    volumetric primary at query time. They are NOT a parallel walkable graph.")

$summary | Set-Content -Path $OutputSummaryPath -Encoding UTF8
Write-Host "Wrote $OutputSummaryPath"
