# Parses an AssetRipper-exported Unity mesh .asset file and returns vertices,
# triangles, and the authored localAABB. Used by Export-SceneBlockerData.ps1
# to consume MeshColliders. Standalone-loadable for spot-checking against a
# known reference asset.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Numerics

function ConvertFrom-MeshHexString {
    param([Parameter(Mandatory = $true)][string]$Hex)

    $clean = ($Hex -replace "\s", "")
    if (($clean.Length % 2) -ne 0) {
        throw "Hex string length is not even ($($clean.Length))"
    }

    $byteCount = $clean.Length / 2
    $bytes = New-Object byte[] $byteCount
    for ($i = 0; $i -lt $byteCount; $i++) {
        $bytes[$i] = [System.Convert]::ToByte($clean.Substring($i * 2, 2), 16)
    }
    return ,$bytes
}

function Read-UnityMeshAsset {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Mesh asset not found: $Path"
    }

    $lines = [System.IO.File]::ReadAllLines($Path)

    $name = $null
    $vertexCount = -1
    $dataSize = -1
    $indexFormat = 0
    $aabbCenter = $null
    $aabbExtent = $null
    $indexBufferHex = $null
    $typelessDataHex = $null
    $positionOffset = 0
    $positionFormatSeen = $false
    $positionStream = 0
    $sawTopLevelAABB = $false
    $inSubMesh = $false
    $subMeshes = New-Object System.Collections.Generic.List[object]
    $currentSub = $null
    $inVertexData = $false
    $inChannel0 = $false
    $channelIndex = -1
    $channelStream = -1
    $channelOffset = -1
    $channelFormat = -1
    $channelDimension = -1

    foreach ($line in $lines) {
        # Top-level m_Name
        if ($null -eq $name) {
            $m = [regex]::Match($line, "^  m_Name: (.*)$")
            if ($m.Success) { $name = $m.Groups[1].Value }
        }

        # Sub-mesh capture: each `- serializedVersion: 2` opens a sub-mesh entry.
        # We capture firstByte/indexCount/topology for every sub-mesh.
        if ($line -match "^  - serializedVersion: 2$") {
            if ($null -ne $currentSub) { $subMeshes.Add($currentSub) }
            $currentSub = [ordered]@{ FirstByte = 0; IndexCount = 0; Topology = 0 }
            $inSubMesh = $true
            continue
        }
        if ($inSubMesh) {
            if ($line -match "^    firstByte: (\d+)") { $currentSub.FirstByte = [int]$Matches[1]; continue }
            if ($line -match "^    indexCount: (\d+)") { $currentSub.IndexCount = [int]$Matches[1]; continue }
            if ($line -match "^    topology: (\d+)") { $currentSub.Topology = [int]$Matches[1]; continue }
            if ($line -match "^  [a-zA-Z]") {
                # Left the sub-mesh list entirely.
                if ($null -ne $currentSub) { $subMeshes.Add($currentSub); $currentSub = $null }
                $inSubMesh = $false
            }
        }

        # m_IndexFormat
        if ($line -match "^  m_IndexFormat: (\d+)") { $indexFormat = [int]$Matches[1]; continue }

        # m_IndexBuffer (single-line hex)
        if ($null -eq $indexBufferHex) {
            $m = [regex]::Match($line, "^  m_IndexBuffer: ([0-9a-fA-F]+)\s*$")
            if ($m.Success) { $indexBufferHex = $m.Groups[1].Value; continue }
        }

        # Enter m_VertexData block
        if ($line -match "^  m_VertexData:") { $inVertexData = $true; continue }
        if ($inVertexData) {
            if ($line -match "^    m_VertexCount: (\d+)") { $vertexCount = [int]$Matches[1]; continue }
            if ($line -match "^    m_DataSize: (\d+)") { $dataSize = [int]$Matches[1]; continue }

            # Channel block tracking (we want channel 0 which is position).
            $chanMatch = [regex]::Match($line, "^    - stream: (\d+)$")
            if ($chanMatch.Success) {
                $channelIndex++
                $inChannel0 = ($channelIndex -eq 0)
                $channelStream = [int]$chanMatch.Groups[1].Value
                continue
            }
            if ($inChannel0) {
                if ($line -match "^      offset: (\d+)") { $channelOffset = [int]$Matches[1]; continue }
                if ($line -match "^      format: (\d+)") { $channelFormat = [int]$Matches[1]; continue }
                if ($line -match "^      dimension: (\d+)") {
                    $channelDimension = [int]$Matches[1]
                    $positionOffset = $channelOffset
                    $positionStream = $channelStream
                    if ($channelFormat -eq 0 -and $channelDimension -ge 3 -and $channelStream -eq 0 -and $channelOffset -eq 0) {
                        $positionFormatSeen = $true
                    }
                    $inChannel0 = $false
                    continue
                }
            }

            # _typelessdata (single-line hex)
            if ($null -eq $typelessDataHex) {
                $m = [regex]::Match($line, "^    _typelessdata: ([0-9a-fA-F]+)\s*$")
                if ($m.Success) { $typelessDataHex = $m.Groups[1].Value; continue }
            }

            # Leave m_VertexData when a new 2-space top-level Mesh key appears
            if ($line -match "^  [a-zA-Z]") { $inVertexData = $false }
        }

        # Top-level localAABB (after m_VertexData section, indented with 2 spaces under Mesh)
        if (-not $sawTopLevelAABB) {
            if ($line -match "^  m_LocalAABB:") {
                $sawTopLevelAABB = $true
                continue
            }
        } else {
            if ($null -eq $aabbCenter) {
                $m = [regex]::Match($line, "^    m_Center: \{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
                if ($m.Success) {
                    $aabbCenter = [System.Numerics.Vector3]::new(
                        [float]::Parse($m.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture),
                        [float]::Parse($m.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture),
                        [float]::Parse($m.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)
                    )
                }
            }
            if ($null -eq $aabbExtent) {
                $m = [regex]::Match($line, "^    m_Extent: \{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
                if ($m.Success) {
                    $aabbExtent = [System.Numerics.Vector3]::new(
                        [float]::Parse($m.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture),
                        [float]::Parse($m.Groups[2].Value, [System.Globalization.CultureInfo]::InvariantCulture),
                        [float]::Parse($m.Groups[3].Value, [System.Globalization.CultureInfo]::InvariantCulture)
                    )
                }
            }
        }
    }

    if ($indexFormat -ne 0) {
        throw "Mesh '$name': UInt32 index format (m_IndexFormat=$indexFormat) is not yet supported"
    }
    if ($vertexCount -le 0) { throw "Mesh '$name': missing or invalid vertex count" }
    if ($dataSize -le 0) { throw "Mesh '$name': missing or invalid m_DataSize" }
    if ($null -eq $typelessDataHex) { throw "Mesh '$name': missing _typelessdata" }
    if ($null -eq $indexBufferHex) { throw "Mesh '$name': missing m_IndexBuffer" }
    if (-not $positionFormatSeen) {
        throw "Mesh '$name': channel 0 is not float32 xyz at offset 0 (format=$channelFormat, dim=$channelDimension, stream=$positionStream, offset=$positionOffset)"
    }
    # Ensure last sub-mesh is flushed if we never crossed the boundary line.
    if ($null -ne $currentSub) { $subMeshes.Add($currentSub); $currentSub = $null }
    if ($subMeshes.Count -eq 0) {
        throw "Mesh '$name': no sub-meshes found"
    }

    $vertexBytes = ConvertFrom-MeshHexString -Hex $typelessDataHex
    if ($vertexBytes.Length -ne $dataSize) {
        throw "Mesh '$name': vertex byte length $($vertexBytes.Length) != m_DataSize $dataSize"
    }
    $stride = [int]($dataSize / $vertexCount)
    if (($stride * $vertexCount) -ne $dataSize) {
        throw "Mesh '$name': vertex stride is not integral ($dataSize / $vertexCount)"
    }

    $vertices = New-Object System.Numerics.Vector3[] $vertexCount
    for ($v = 0; $v -lt $vertexCount; $v++) {
        $base = ($v * $stride) + $positionOffset
        $x = [System.BitConverter]::ToSingle($vertexBytes, $base)
        $y = [System.BitConverter]::ToSingle($vertexBytes, $base + 4)
        $z = [System.BitConverter]::ToSingle($vertexBytes, $base + 8)
        $vertices[$v] = [System.Numerics.Vector3]::new($x, $y, $z)
    }

    $indexBytes = ConvertFrom-MeshHexString -Hex $indexBufferHex

    $triangleList = New-Object System.Collections.Generic.List[int]
    foreach ($sub in $subMeshes) {
        if ($sub.Topology -ne 0) {
            # Skip non-triangle topologies (lines, points, etc) without failing the mesh.
            continue
        }
        if ($sub.IndexCount -le 0) { continue }
        $byteEnd = $sub.FirstByte + ($sub.IndexCount * 2)
        if ($byteEnd -gt $indexBytes.Length) {
            throw "Mesh '$name': sub-mesh extends past index buffer (firstByte=$($sub.FirstByte) indexCount=$($sub.IndexCount) bufferBytes=$($indexBytes.Length))"
        }
        $triCount = [int]($sub.IndexCount / 3)
        for ($i = 0; $i -lt $triCount; $i++) {
            $byteBase = $sub.FirstByte + ($i * 6)
            $a = [System.BitConverter]::ToUInt16($indexBytes, $byteBase)
            $b = [System.BitConverter]::ToUInt16($indexBytes, $byteBase + 2)
            $c = [System.BitConverter]::ToUInt16($indexBytes, $byteBase + 4)
            if ($a -ge $vertexCount -or $b -ge $vertexCount -or $c -ge $vertexCount) {
                throw "Mesh '$name': triangle index out of range (a=$a b=$b c=$c vertexCount=$vertexCount)"
            }
            $triangleList.Add($a); $triangleList.Add($b); $triangleList.Add($c)
        }
    }
    $triangles = $triangleList.ToArray()
    $triangleCount = [int]($triangles.Length / 3)

    return [pscustomobject]@{
        Name = $name
        VertexCount = $vertexCount
        TriangleCount = $triangleCount
        Stride = $stride
        Vertices = $vertices
        Triangles = $triangles
        LocalAabbCenter = $aabbCenter
        LocalAabbExtent = $aabbExtent
    }
}
