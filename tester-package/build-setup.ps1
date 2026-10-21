# Builds DateEverythingAccess-Setup.exe with the C# compiler that comes with Windows.
# Make-Tester-Installer.bat finds the game folder and passes it as -GameDir.
param([string]$Package, [string]$Out, [string]$Log, [Parameter(Mandatory = $true)][string]$GameDir)
$ErrorActionPreference = "Stop"
try {
    "Setup build started $(Get-Date)" | Set-Content -LiteralPath $Log

    # Fresh payload from the installed, working copy of the mod in the game folder.
    $stage = Join-Path $env:TEMP ("DEAPayload-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path (Join-Path $stage "BepInEx\core") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stage "BepInEx\plugins") -Force | Out-Null
    foreach ($f in @(".doorstop_version", "doorstop_config.ini", "winhttp.dll", "prism.dll")) {
        Copy-Item -LiteralPath (Join-Path $GameDir $f) -Destination $stage -Force
    }
    # Speech goes through Prism; its license texts travel with it.
    Copy-Item -LiteralPath (Join-Path $GameDir "Prism-Licenses") -Destination $stage -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $Package "README.txt") -Destination $stage -Force
    Copy-Item -LiteralPath (Join-Path $Package "THIRD-PARTY-NOTICES.txt") -Destination $stage -Force
    Copy-Item -Path (Join-Path $GameDir "BepInEx\core\*") -Destination (Join-Path $stage "BepInEx\core") -Force
    foreach ($f in @("DateEverythingAccess.dll", "card_pose_descriptions.en.json", "navigable_region.bake.json")) {
        Copy-Item -LiteralPath (Join-Path $GameDir ("BepInEx\plugins\" + $f)) -Destination (Join-Path $stage "BepInEx\plugins") -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $payload = Join-Path $Package "payload.zip"
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload)
    Remove-Item -LiteralPath $stage -Recurse -Force
    "Payload built from $GameDir" | Add-Content -LiteralPath $Log
    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $params = New-Object System.CodeDom.Compiler.CompilerParameters
    $params.GenerateExecutable = $true
    $params.GenerateInMemory = $false
    $params.OutputAssembly = $Out
    $params.CompilerOptions = "/target:winexe /optimize"
    [void]$params.ReferencedAssemblies.Add("System.dll")
    [void]$params.ReferencedAssemblies.Add("System.Windows.Forms.dll")
    [void]$params.EmbeddedResources.Add((Join-Path $Package "payload.zip"))
    [void]$params.EmbeddedResources.Add((Join-Path $Package "install.ps1"))
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Force }
    $result = $provider.CompileAssemblyFromFile($params, @((Join-Path $Package "SetupStub.cs")))
    foreach ($e in $result.Errors) { "$($e.ErrorNumber) line $($e.Line): $($e.ErrorText)" | Add-Content -LiteralPath $Log }
    if ($result.Errors.HasErrors -or -not (Test-Path -LiteralPath $Out)) {
        "Setup build FAILED" | Add-Content -LiteralPath $Log
        exit 1
    }
    "Setup build finished OK: $Out" | Add-Content -LiteralPath $Log
    exit 0
} catch {
    "Setup build FAILED: $($_.Exception.Message)" | Add-Content -LiteralPath $Log
    exit 1
}
