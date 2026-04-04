Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter()]
        [AllowNull()]
        [object]$Default = $null
    )

    if ($null -eq $InputObject) {
        return $Default
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $Default
    }

    return $property.Value
}

function Read-JsonDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json -Depth 64
}

function Write-JsonDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Document
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = $Document | ConvertTo-Json -Depth 64
    Set-Content -LiteralPath $Path -Value $json -Encoding utf8
}

function Write-TextDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string[]]$Lines
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    Set-Content -LiteralPath $Path -Value $Lines -Encoding utf8
}

function Convert-ToInvariantDouble {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return 0.0
    }

    return [double]::Parse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function Convert-VectorRecord {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Vector
    )

    if ($null -eq $Vector) {
        return $null
    }

    return [ordered]@{
        x = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Vector -Name "x" -Default 0.0)
        y = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Vector -Name "y" -Default 0.0)
        z = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Vector -Name "z" -Default 0.0)
    }
}

function Convert-SweepEntryRecord {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Entry
    )

    if ($null -eq $Entry) {
        return $null
    }

    return [ordered]@{
        Index = [int](Get-JsonPropertyValue -InputObject $Entry -Name "Index" -Default 0)
        StepIndex = [int](Get-JsonPropertyValue -InputObject $Entry -Name "StepIndex" -Default 0)
        Key = [string](Get-JsonPropertyValue -InputObject $Entry -Name "Key" -Default "")
        FromZone = Get-JsonPropertyValue -InputObject $Entry -Name "FromZone" -Default $null
        ToZone = Get-JsonPropertyValue -InputObject $Entry -Name "ToZone" -Default $null
        StepKind = Get-JsonPropertyValue -InputObject $Entry -Name "StepKind" -Default $null
        TransitionKind = Get-JsonPropertyValue -InputObject $Entry -Name "TransitionKind" -Default $null
        ConnectorName = Get-JsonPropertyValue -InputObject $Entry -Name "ConnectorName" -Default $null
        RequiresInteraction = [bool](Get-JsonPropertyValue -InputObject $Entry -Name "RequiresInteraction" -Default $false)
        Status = Get-JsonPropertyValue -InputObject $Entry -Name "Status" -Default "pending"
        StatusDetail = Get-JsonPropertyValue -InputObject $Entry -Name "StatusDetail" -Default $null
        FailureReason = Get-JsonPropertyValue -InputObject $Entry -Name "FailureReason" -Default $null
        DurationSeconds = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Entry -Name "DurationSeconds" -Default 0.0)
        SpawnSource = Get-JsonPropertyValue -InputObject $Entry -Name "SpawnSource" -Default $null
        SpawnPosition = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "SpawnPosition" -Default $null)
        FromWaypoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "FromWaypoint" -Default $null)
        ToWaypoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "ToWaypoint" -Default $null)
        FromCrossingAnchor = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "FromCrossingAnchor" -Default $null)
        ToCrossingAnchor = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "ToCrossingAnchor" -Default $null)
        SourceApproachPoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "SourceApproachPoint" -Default $null)
        SourceClearPoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "SourceClearPoint" -Default $null)
        DestinationClearPoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "DestinationClearPoint" -Default $null)
        DestinationApproachPoint = Convert-VectorRecord (Get-JsonPropertyValue -InputObject $Entry -Name "DestinationApproachPoint" -Default $null)
        ValidationTimeoutSeconds = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Entry -Name "ValidationTimeoutSeconds" -Default 0.0)
        StaticSuspicionScore = [int](Get-JsonPropertyValue -InputObject $Entry -Name "StaticSuspicionScore" -Default 0)
    }
}

function Get-OrderedSweepEntries {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Entries
    )

    if ($null -eq $Entries) {
        return @()
    }

    $convertedEntries = foreach ($entry in $Entries) {
        $converted = Convert-SweepEntryRecord -Entry $entry
        if ($null -ne $converted -and -not [string]::IsNullOrWhiteSpace($converted.Key)) {
            [pscustomobject]$converted
        }
    }

    return @(
        $convertedEntries |
            Sort-Object -Property `
                @{ Expression = { $_.FromZone } ; Descending = $false },
                @{ Expression = { $_.ToZone } ; Descending = $false },
                @{ Expression = { $_.StepIndex } ; Descending = $false },
                @{ Expression = { $_.Key } ; Descending = $false }
    )
}

function Get-SweepStatusCounts {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Entries
    )

    $passed = 0
    $failed = 0
    $pending = 0

    foreach ($entry in @($Entries)) {
        $status = [string](Get-JsonPropertyValue -InputObject $entry -Name "Status" -Default "pending")
        if ($status -ieq "passed") {
            $passed++
            continue
        }

        if ($status -ieq "failed") {
            $failed++
            continue
        }

        $pending++
    }

    return [ordered]@{
        Passed = $passed
        Failed = $failed
        Pending = $pending
        Total = $passed + $failed + $pending
    }
}

function New-SweepEntryMap {
    param(
        [Parameter()]
        [AllowNull()]
        [object[]]$Entries
    )

    $map = @{}
    foreach ($entry in @($Entries)) {
        $key = Get-JsonPropertyValue -InputObject $entry -Name "Key" -Default $null
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $map[$key] = $entry
        }
    }

    return $map
}

function Merge-SweepReportDocuments {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$ExistingReport,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$ImportedReport,

        [Parameter(Mandatory = $true)]
        [string]$DefaultSweepKind
    )

    $entryMap = @{}

    foreach ($entry in @(Get-OrderedSweepEntries -Entries (Get-JsonPropertyValue -InputObject $ExistingReport -Name "Entries" -Default @()))) {
        $entryMap[$entry.Key] = $entry
    }

    foreach ($entry in @(Get-OrderedSweepEntries -Entries (Get-JsonPropertyValue -InputObject $ImportedReport -Name "Entries" -Default @()))) {
        $entryMap[$entry.Key] = $entry
    }

    $mergedEntries = @(
        $entryMap.Values |
            Sort-Object -Property `
                @{ Expression = { $_.FromZone } ; Descending = $false },
                @{ Expression = { $_.ToZone } ; Descending = $false },
                @{ Expression = { $_.StepIndex } ; Descending = $false },
                @{ Expression = { $_.Key } ; Descending = $false }
    )

    for ($i = 0; $i -lt $mergedEntries.Count; $i++) {
        $mergedEntries[$i].Index = $i
    }

    $counts = Get-SweepStatusCounts -Entries $mergedEntries
    $sweepKind = Get-JsonPropertyValue -InputObject $ImportedReport -Name "SweepKind" -Default $null
    if ([string]::IsNullOrWhiteSpace($sweepKind)) {
        $sweepKind = $DefaultSweepKind
    }

    return [pscustomobject][ordered]@{
        GeneratedAtUtc = Get-JsonPropertyValue -InputObject $ImportedReport -Name "GeneratedAtUtc" -Default $null
        ImportedAtUtc = [DateTime]::UtcNow.ToString("o")
        ActiveScene = Get-JsonPropertyValue -InputObject $ImportedReport -Name "ActiveScene" -Default $null
        SweepKind = $sweepKind
        IsComplete = [bool](Get-JsonPropertyValue -InputObject $ImportedReport -Name "IsComplete" -Default $false) -and ($counts.Pending -eq 0)
        TotalCount = $counts.Total
        PassedCount = $counts.Passed
        FailedCount = $counts.Failed
        PendingCount = $counts.Pending
        Entries = $mergedEntries
    }
}

function Get-SweepStatusChangeSummary {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$ExistingReport,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$ImportedReport
    )

    $existingMap = New-SweepEntryMap -Entries (Get-JsonPropertyValue -InputObject $ExistingReport -Name "Entries" -Default @())
    $newlyPassed = 0
    $newlyFailed = 0
    $newlyPending = 0
    $unchanged = 0
    $newEntries = 0

    foreach ($entry in @(Get-OrderedSweepEntries -Entries (Get-JsonPropertyValue -InputObject $ImportedReport -Name "Entries" -Default @()))) {
        $oldEntry = if ($existingMap.ContainsKey($entry.Key)) { $existingMap[$entry.Key] } else { $null }

        $newStatus = [string](Get-JsonPropertyValue -InputObject $entry -Name "Status" -Default "pending")
        if ($null -eq $oldEntry) {
            $newEntries++
        }
        else {
            $oldStatus = [string](Get-JsonPropertyValue -InputObject $oldEntry -Name "Status" -Default "pending")
            if ($oldStatus -ieq $newStatus) {
                $unchanged++
            }
        }

        $oldStatusForTransition = if ($null -eq $oldEntry) { $null } else { [string](Get-JsonPropertyValue -InputObject $oldEntry -Name "Status" -Default "pending") }
        if ($newStatus -ieq "passed" -and $oldStatusForTransition -ine "passed") {
            $newlyPassed++
        }
        elseif ($newStatus -ieq "failed" -and $oldStatusForTransition -ine "failed") {
            $newlyFailed++
        }
        elseif ($newStatus -ieq "pending" -and $oldStatusForTransition -ine "pending") {
            $newlyPending++
        }
    }

    return [ordered]@{
        ImportedEntries = @((Get-JsonPropertyValue -InputObject $ImportedReport -Name "Entries" -Default @())).Count
        NewEntries = $newEntries
        NewlyPassed = $newlyPassed
        NewlyFailed = $newlyFailed
        NewlyPending = $newlyPending
        Unchanged = $unchanged
    }
}

function Try-ParseStalledPlayerPosition {
    param(
        [Parameter()]
        [AllowNull()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $match = [regex]::Match(
        $Text,
        "player=\((?<x>-?\d+(?:\.\d+)?),\s*(?<y>-?\d+(?:\.\d+)?),\s*(?<z>-?\d+(?:\.\d+)?)\)",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if (-not $match.Success) {
        return $null
    }

    return [ordered]@{
        x = Convert-ToInvariantDouble $match.Groups["x"].Value
        y = Convert-ToInvariantDouble $match.Groups["y"].Value
        z = Convert-ToInvariantDouble $match.Groups["z"].Value
    }
}

function Copy-TransitionOverrideEntry {
    param(
        [Parameter()]
        [AllowNull()]
        [object]$Entry
    )

    if ($null -eq $Entry) {
        return $null
    }

    $acceptedSourceZones = @(Get-JsonPropertyValue -InputObject $Entry -Name "AcceptedSourceZones" -Default @())
    $acceptedDestinationZones = @(Get-JsonPropertyValue -InputObject $Entry -Name "AcceptedDestinationZones" -Default @())
    $intermediateWaypoints = @(Get-JsonPropertyValue -InputObject $Entry -Name "IntermediateWaypoints" -Default @())

    return [ordered]@{
        FromZone = Get-JsonPropertyValue -InputObject $Entry -Name "FromZone" -Default $null
        ToZone = Get-JsonPropertyValue -InputObject $Entry -Name "ToZone" -Default $null
        AcceptedSourceZones = if ($acceptedSourceZones.Count -gt 0) { @($acceptedSourceZones) } else { $null }
        AcceptedDestinationZones = if ($acceptedDestinationZones.Count -gt 0) { @($acceptedDestinationZones) } else { $null }
        DestinationApproachBias = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Entry -Name "DestinationApproachBias" -Default 1.0)
        UseExplicitCrossingSegments = [bool](Get-JsonPropertyValue -InputObject $Entry -Name "UseExplicitCrossingSegments" -Default $true)
        StepTimeoutSeconds = Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $Entry -Name "StepTimeoutSeconds" -Default 0.0)
        IntermediateWaypoints = @($intermediateWaypoints | ForEach-Object { Convert-VectorRecord $_ })
    }
}

function Remove-NullTransitionOverrideProperties {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Entry
    )

    foreach ($name in @("AcceptedSourceZones", "AcceptedDestinationZones", "IntermediateWaypoints")) {
        if ($null -eq $Entry[$name] -or @($Entry[$name]).Count -eq 0) {
            $Entry.Remove($name)
        }
    }

    return $Entry
}

function Update-OpenPassageOverridesFromReport {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$MergedReport,

        [Parameter(Mandatory = $true)]
        [string]$OverridePath
    )

    $existingDocument = Read-JsonDocument -Path $OverridePath
    $existingEntries = @(Get-JsonPropertyValue -InputObject $existingDocument -Name "Entries" -Default @())
    $overrideMap = @{}

    foreach ($entry in $existingEntries) {
        $copied = Copy-TransitionOverrideEntry -Entry $entry
        if ($null -eq $copied) {
            continue
        }

        $key = "$($copied.FromZone)->$($copied.ToZone)"
        $overrideMap[$key] = $copied
    }

    $added = 0
    $refreshed = 0
    $preservedManualWaypoints = 0
    $failedWithoutWaypoint = 0
    $failedKeys = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)

    $failedEntries = @(
        @(Get-JsonPropertyValue -InputObject $MergedReport -Name "Entries" -Default @()) |
            Where-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "Status" -Default "pending") -ieq "failed" }
    )

    foreach ($entry in $failedEntries) {
        $fromZone = [string](Get-JsonPropertyValue -InputObject $entry -Name "FromZone" -Default "")
        $toZone = [string](Get-JsonPropertyValue -InputObject $entry -Name "ToZone" -Default "")
        if ([string]::IsNullOrWhiteSpace($fromZone) -or [string]::IsNullOrWhiteSpace($toZone)) {
            continue
        }

        $key = "$fromZone->$toZone"
        [void]$failedKeys.Add($key)

        $overrideEntry = if ($overrideMap.ContainsKey($key)) { $overrideMap[$key] } else { $null }
        $wasAdded = $false
        if ($null -eq $overrideEntry) {
            $overrideEntry = [ordered]@{
                FromZone = $fromZone
                ToZone = $toZone
                DestinationApproachBias = 1.0
                UseExplicitCrossingSegments = $true
                StepTimeoutSeconds = [Math]::Max((Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $entry -Name "ValidationTimeoutSeconds" -Default 0.0)), 5.0)
                IntermediateWaypoints = @()
            }
            $overrideMap[$key] = $overrideEntry
            $added++
            $wasAdded = $true
        }
        else {
            if ((Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $overrideEntry -Name "StepTimeoutSeconds" -Default 0.0)) -le 0.0) {
                $overrideEntry.StepTimeoutSeconds = [Math]::Max((Convert-ToInvariantDouble (Get-JsonPropertyValue -InputObject $entry -Name "ValidationTimeoutSeconds" -Default 0.0)), 5.0)
            }

            if (-not [bool](Get-JsonPropertyValue -InputObject $overrideEntry -Name "UseExplicitCrossingSegments" -Default $false)) {
                $overrideEntry.UseExplicitCrossingSegments = $true
            }
        }

        $playerPosition = Try-ParseStalledPlayerPosition -Text ([string](Get-JsonPropertyValue -InputObject $entry -Name "StatusDetail" -Default $null))
        if ($null -eq $playerPosition) {
            $playerPosition = Try-ParseStalledPlayerPosition -Text ([string](Get-JsonPropertyValue -InputObject $entry -Name "FailureReason" -Default $null))
        }

        if ($null -eq $playerPosition) {
            $failedWithoutWaypoint++
            continue
        }

        $existingWaypoints = @(
            @(Get-JsonPropertyValue -InputObject $overrideEntry -Name "IntermediateWaypoints" -Default @()) |
                ForEach-Object { Convert-VectorRecord $_ }
        )

        if ($existingWaypoints.Count -gt 1) {
            $preservedManualWaypoints++
            continue
        }

        $overrideEntry.IntermediateWaypoints = @([pscustomobject]$playerPosition)
        if (-not $wasAdded) {
            $refreshed++
        }
    }

    $retainedWithoutCurrentFailure = @(
        $overrideMap.Keys |
            Where-Object { -not $failedKeys.Contains($_) }
    ).Count

    $orderedEntries = @(
        $overrideMap.Values |
            Sort-Object -Property `
                @{ Expression = { $_.FromZone } ; Descending = $false },
                @{ Expression = { $_.ToZone } ; Descending = $false }
    ) | ForEach-Object { [pscustomobject](Remove-NullTransitionOverrideProperties -Entry $_) }

    $document = [pscustomobject][ordered]@{
        Entries = $orderedEntries
    }

    Write-JsonDocument -Path $OverridePath -Document $document

    return [ordered]@{
        Added = $added
        Refreshed = $refreshed
        PreservedManualWaypoints = $preservedManualWaypoints
        FailedWithoutWaypoint = $failedWithoutWaypoint
        RetainedWithoutCurrentFailure = $retainedWithoutCurrentFailure
        TotalEntries = $orderedEntries.Count
    }
}

function Get-SweepFailureSummary {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$MergedReport
    )

    $failedEntries = @(
        @(Get-JsonPropertyValue -InputObject $MergedReport -Name "Entries" -Default @()) |
            Where-Object { [string](Get-JsonPropertyValue -InputObject $_ -Name "Status" -Default "pending") -ieq "failed" }
    )

    $interactionFalseCount = @(
        $failedEntries |
            Where-Object {
                $detail = [string](Get-JsonPropertyValue -InputObject $_ -Name "StatusDetail" -Default "")
                $detail.IndexOf("interaction=False", [StringComparison]::OrdinalIgnoreCase) -ge 0
            }
    ).Count

    $missingConnectorCount = @(
        $failedEntries |
            Where-Object {
                [string]::IsNullOrWhiteSpace([string](Get-JsonPropertyValue -InputObject $_ -Name "ConnectorName" -Default $null))
            }
    ).Count

    $highSuspicionCount = @(
        $failedEntries |
            Where-Object { [int](Get-JsonPropertyValue -InputObject $_ -Name "StaticSuspicionScore" -Default 0) -ge 4 }
    ).Count

    return [ordered]@{
        FailedEntries = $failedEntries
        InteractionFalseCount = $interactionFalseCount
        MissingConnectorCount = $missingConnectorCount
        HighSuspicionCount = $highSuspicionCount
    }
}

function Format-SweepSummaryLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$OutputPath,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$ImportedReport,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$MergedReport,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$StatusChanges,

        [Parameter()]
        [AllowNull()]
        [System.Collections.IDictionary]$OverrideUpdate,

        [Parameter(Mandatory = $true)]
        [string]$SummaryKind
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Sweep report import summary")
    $lines.Add("Kind: $SummaryKind")
    $lines.Add("Imported at: $([DateTime]::UtcNow.ToString('o'))")
    $lines.Add("Source: $SourcePath")
    $lines.Add("Output: $OutputPath")
    $lines.Add("Report generated at: $([string](Get-JsonPropertyValue -InputObject $ImportedReport -Name 'GeneratedAtUtc' -Default 'unknown'))")
    $lines.Add("Active scene: $([string](Get-JsonPropertyValue -InputObject $MergedReport -Name 'ActiveScene' -Default 'unknown'))")
    $lines.Add("Merged totals: total=$([int](Get-JsonPropertyValue -InputObject $MergedReport -Name 'TotalCount' -Default 0)) passed=$([int](Get-JsonPropertyValue -InputObject $MergedReport -Name 'PassedCount' -Default 0)) failed=$([int](Get-JsonPropertyValue -InputObject $MergedReport -Name 'FailedCount' -Default 0)) pending=$([int](Get-JsonPropertyValue -InputObject $MergedReport -Name 'PendingCount' -Default 0))")
    $lines.Add("Imported status changes: newEntries=$($StatusChanges.NewEntries) newlyPassed=$($StatusChanges.NewlyPassed) newlyFailed=$($StatusChanges.NewlyFailed) newlyPending=$($StatusChanges.NewlyPending) unchanged=$($StatusChanges.Unchanged)")

    if ($null -ne $OverrideUpdate) {
        $lines.Add("Open-passage override refresh: added=$($OverrideUpdate.Added) refreshed=$($OverrideUpdate.Refreshed) preservedManualWaypoints=$($OverrideUpdate.PreservedManualWaypoints) failedWithoutWaypoint=$($OverrideUpdate.FailedWithoutWaypoint) retainedWithoutCurrentFailure=$($OverrideUpdate.RetainedWithoutCurrentFailure) totalEntries=$($OverrideUpdate.TotalEntries)")
    }

    $failureSummary = Get-SweepFailureSummary -MergedReport $MergedReport
    if ($SummaryKind -ieq "Door") {
        $lines.Add("Door failure breakdown: interactionFalse=$($failureSummary.InteractionFalseCount) missingConnectorName=$($failureSummary.MissingConnectorCount) highSuspicion=$($failureSummary.HighSuspicionCount)")
    }

    $lines.Add("Remaining failed transitions:")
    if (@($failureSummary.FailedEntries).Count -eq 0) {
        $lines.Add("- none")
    }
    else {
        foreach ($entry in $failureSummary.FailedEntries) {
            $fromZone = [string](Get-JsonPropertyValue -InputObject $entry -Name "FromZone" -Default "<null>")
            $toZone = [string](Get-JsonPropertyValue -InputObject $entry -Name "ToZone" -Default "<null>")
            $connectorName = [string](Get-JsonPropertyValue -InputObject $entry -Name "ConnectorName" -Default $null)
            $detail = [string](Get-JsonPropertyValue -InputObject $entry -Name "StatusDetail" -Default "failed")
            $suspicion = [int](Get-JsonPropertyValue -InputObject $entry -Name "StaticSuspicionScore" -Default 0)
            $connectorText = if ([string]::IsNullOrWhiteSpace($connectorName)) { "connector=<none>" } else { "connector=$connectorName" }
            $lines.Add("- $fromZone -> $toZone | suspicion=$suspicion | $connectorText | $detail")
        }
    }

    return $lines.ToArray()
}
