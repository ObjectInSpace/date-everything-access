<#
.SYNOPSIS
    Validates the BepInEx-based Date Everything Access project setup.

.DESCRIPTION
    Checks the current game and project folders for the files this project
    actually depends on:
    - BepInEx installation
    - Tolk DLLs
    - project file and build references
    - plugin attribute and copy target
    - decompiled game code
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,

    [string]$ProjectPath = (Get-Location).Path,

    [ValidateSet("x64", "x86")]
    [string]$Architecture = "x64"
)

$script:errorCount = 0
$script:warningCount = 0
$script:successCount = 0

function Write-Check {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Details = ""
    )

    switch ($Status) {
        "OK" {
            Write-Host "OK: $Name"
            $script:successCount++
        }
        "ERROR" {
            Write-Host "ERROR: $Name"
            $script:errorCount++
        }
        "WARN" {
            Write-Host "WARN: $Name"
            $script:warningCount++
        }
    }

    if ($Details) {
        Write-Host "   $Details"
    }
}

function Write-Solution {
    param([string]$Text)
    Write-Host "   Fix: $Text"
}

Write-Host ""
Write-Host "Date Everything Access Setup Validation"
Write-Host "======================================="
Write-Host ""
Write-Host "Game folder: $GamePath"
Write-Host "Project folder: $ProjectPath"
Write-Host "Architecture: $Architecture"
Write-Host ""

Write-Host "1. Game Folder"
Write-Host ""

if (Test-Path -LiteralPath $GamePath) {
    Write-Check "Game folder exists" "OK"
} else {
    Write-Check "Game folder exists" "ERROR" "Path not found: $GamePath"
    Write-Solution "Check the game path and run the script again."
    exit 1
}

$exeFiles = Get-ChildItem -Path $GamePath -Filter "*.exe" -File | Where-Object { $_.Name -notmatch "UnityCrashHandler|UnityPlayer" }
if ($exeFiles.Count -gt 0) {
    Write-Check "Game executable found" "OK" $exeFiles[0].Name
} else {
    Write-Check "Game executable found" "WARN" "No game executable was found in the top-level game folder."
}

Write-Host ""
Write-Host "2. BepInEx"
Write-Host ""

$bepInExPath = Join-Path $GamePath "BepInEx"
$bepInExCore = Join-Path $bepInExPath "core"
$bepInExPlugins = Join-Path $bepInExPath "plugins"
$bepInExDll = Join-Path $bepInExCore "BepInEx.dll"
$harmonyDll = Join-Path $bepInExCore "0Harmony.dll"
$logPath = Join-Path $bepInExPath "LogOutput.log"

if (Test-Path -LiteralPath $bepInExPath) {
    Write-Check "BepInEx folder exists" "OK"
} else {
    Write-Check "BepInEx folder exists" "ERROR" "Folder not found: $bepInExPath"
    Write-Solution "Install BepInEx 5.x for the game and launch it once."
}

if (Test-Path -LiteralPath $bepInExDll) {
    Write-Check "BepInEx.dll present" "OK"
} else {
    Write-Check "BepInEx.dll present" "ERROR" "Missing: $bepInExDll"
    Write-Solution "Reinstall BepInEx and confirm the core runtime files are in place."
}

if (Test-Path -LiteralPath $harmonyDll) {
    Write-Check "0Harmony.dll present" "OK"
} else {
    Write-Check "0Harmony.dll present" "ERROR" "Missing: $harmonyDll"
    Write-Solution "Reinstall BepInEx and confirm Harmony was extracted into BepInEx\\core."
}

if (Test-Path -LiteralPath $bepInExPlugins) {
    Write-Check "BepInEx plugins folder exists" "OK"
} else {
    Write-Check "BepInEx plugins folder exists" "WARN" "Folder not found: $bepInExPlugins"
    Write-Solution "Start the game once after installing BepInEx to create the folder structure."
}

if (Test-Path -LiteralPath $logPath) {
    Write-Check "BepInEx log found" "OK" $logPath
} else {
    Write-Check "BepInEx log found" "WARN" "Log file not found: $logPath"
    Write-Solution "Start the game once with BepInEx to generate LogOutput.log."
}

Write-Host ""
Write-Host "3. Tolk"
Write-Host ""

$tolkDll = Join-Path $GamePath "Tolk.dll"
$nvdaDll = if ($Architecture -eq "x64") {
    Join-Path $GamePath "nvdaControllerClient64.dll"
} else {
    Join-Path $GamePath "nvdaControllerClient32.dll"
}

if (Test-Path -LiteralPath $tolkDll) {
    Write-Check "Tolk.dll present" "OK"
} else {
    Write-Check "Tolk.dll present" "ERROR" "Missing: $tolkDll"
    Write-Solution "Copy Tolk.dll into the game folder."
}

if (Test-Path -LiteralPath $nvdaDll) {
    Write-Check "$(Split-Path $nvdaDll -Leaf) present" "OK"
} else {
    Write-Check "$(Split-Path $nvdaDll -Leaf) present" "ERROR" "Missing: $nvdaDll"
    Write-Solution "Copy the matching NVDA controller client DLL into the game folder."
}

Write-Host ""
Write-Host "4. Project Files"
Write-Host ""

$csprojFiles = Get-ChildItem -Path $ProjectPath -Filter "*.csproj" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notmatch "Assembly-CSharp" }

if ($csprojFiles.Count -eq 0) {
    Write-Check "Project file (.csproj)" "ERROR" "No project file was found."
    Write-Solution "Create or restore the project file."
    $csprojPath = $null
} elseif ($csprojFiles.Count -eq 1) {
    $csprojPath = $csprojFiles[0].FullName
    Write-Check "Project file (.csproj)" "OK" $csprojFiles[0].Name
} else {
    $csprojPath = $csprojFiles[0].FullName
    $names = ($csprojFiles | ForEach-Object { $_.Name }) -join ", "
    Write-Check "Project file (.csproj)" "WARN" "Multiple project files found: $names"
}

$mainCs = Join-Path $ProjectPath "Main.cs"
if (Test-Path -LiteralPath $mainCs) {
    Write-Check "Main.cs present" "OK"

    $mainContent = Get-Content -LiteralPath $mainCs -Raw
    if ($mainContent -match '\[BepInPlugin\("([^"]+)",\s*"([^"]+)",\s*"([^"]+)"\)\]') {
        Write-Check "BepInPlugin attribute" "OK" "GUID: $($matches[1]); Name: $($matches[2]); Version: $($matches[3])"
    } else {
        Write-Check "BepInPlugin attribute" "ERROR" "Main.cs is missing a valid BepInPlugin attribute."
        Write-Solution "Add [BepInPlugin(...)] to the plugin class in Main.cs."
    }
} else {
    Write-Check "Main.cs present" "ERROR" "Missing: $mainCs"
    Write-Solution "Restore Main.cs from source control or recreate it."
}

Write-Host ""
Write-Host "5. Project Configuration"
Write-Host ""

if ($csprojPath -and (Test-Path -LiteralPath $csprojPath)) {
    $csprojContent = Get-Content -LiteralPath $csprojPath -Raw

    if ($csprojContent -match '<TargetFramework>([^<]+)</TargetFramework>') {
        Write-Check "TargetFramework" "OK" $matches[1]
    } else {
        Write-Check "TargetFramework" "ERROR" "No TargetFramework entry was found."
    }

    if ($csprojContent -match '<Compile\s+Remove="decompiled\\\*\*"') {
        Write-Check "decompiled exclusion" "OK"
    } else {
        Write-Check "decompiled exclusion" "WARN" "The decompiled folder is not excluded from compilation."
        Write-Solution 'Add <Compile Remove="decompiled\**" /> to the project file.'
    }

    if ($csprojContent -match '<Reference\s+Include="BepInEx"') {
        Write-Check "BepInEx reference" "OK"
    } else {
        Write-Check "BepInEx reference" "ERROR" "The project file does not reference BepInEx."
        Write-Solution "Add the BepInEx core reference to the project file."
    }

    if ($csprojContent -match '<Reference\s+Include="0Harmony"') {
        Write-Check "Harmony reference" "OK"
    } else {
        Write-Check "Harmony reference" "ERROR" "The project file does not reference 0Harmony."
        Write-Solution "Add the Harmony reference from BepInEx\\core."
    }

    if ($csprojContent -match 'CopyToPlugins') {
        Write-Check "CopyToPlugins target" "OK"
    } else {
        Write-Check "CopyToPlugins target" "WARN" "No post-build plugin copy target was found."
        Write-Solution "Add a CopyToPlugins target if you want the DLL copied into BepInEx\\plugins automatically."
    }
}

Write-Host ""
Write-Host "6. Decompiled Source"
Write-Host ""

$decompiledPath = Join-Path $ProjectPath "decompiled"
if (Test-Path -LiteralPath $decompiledPath) {
    $decompiledFiles = Get-ChildItem -Path $decompiledPath -Filter "*.cs" -Recurse -ErrorAction SilentlyContinue
    if ($decompiledFiles.Count -gt 0) {
        Write-Check "Decompiled source present" "OK" "$($decompiledFiles.Count) C# files found"
    } else {
        Write-Check "Decompiled source present" "WARN" "The folder exists but no C# files were found."
        Write-Solution "Re-decompile the game assemblies into the decompiled folder."
    }
} else {
    Write-Check "Decompiled source present" "WARN" "The decompiled folder does not exist."
    Write-Solution "Create the decompiled folder and extract the game assemblies there."
}

Write-Host ""
Write-Host "Summary"
Write-Host "======="
Write-Host ""
Write-Host "Successful: $script:successCount"
Write-Host "Warnings:   $script:warningCount"
Write-Host "Errors:     $script:errorCount"
Write-Host ""

if ($script:errorCount -eq 0 -and $script:warningCount -eq 0) {
    Write-Host "Everything looks ready."
} elseif ($script:errorCount -eq 0) {
    Write-Host "The project is usable, but there are warnings worth fixing."
} else {
    Write-Host "Fix the reported errors before relying on the build or runtime setup."
}

Write-Host ""
