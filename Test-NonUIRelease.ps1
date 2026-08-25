[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BassNativeDirectory,
    [Parameter(Mandatory)]
    [string]$SoundFontPath,
    [string]$ArtifactsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-DotNet {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
& (Join-Path $repositoryRoot "Test-VersionControl.ps1")
$resolvedNativeDirectory = [System.IO.Path]::GetFullPath($BassNativeDirectory)
$resolvedSoundFontPath = [System.IO.Path]::GetFullPath($SoundFontPath)
if (-not (Test-Path -LiteralPath $resolvedSoundFontPath -PathType Leaf)) {
    throw "Integration-test SoundFont does not exist: $resolvedSoundFontPath"
}

$baselinePath = Join-Path $repositoryRoot "misc\Midora-Non-UI-Test-Baseline.json"
$baseline = Get-Content -LiteralPath $baselinePath -Raw -Encoding utf8 | ConvertFrom-Json
if ($baseline.schemaVersion -ne 1) {
    throw "Unsupported non-UI test baseline schema: $($baseline.schemaVersion)."
}

$actualSdkVersion = (& dotnet --version)
if ($LASTEXITCODE -ne 0 -or $actualSdkVersion -ne [string]$baseline.sdkVersion) {
    throw "The non-UI release gate requires .NET SDK $($baseline.sdkVersion); actual=$actualSdkVersion."
}

& (Join-Path $repositoryRoot "src\midora-audio\Test-BassNative.ps1") `
    -Directory $resolvedNativeDirectory `
    -Architecture win-x64

if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path `
        $repositoryRoot `
        ("artifacts\non-ui-release-gate-" + [Guid]::NewGuid().ToString("N"))
}
$resolvedArtifactsDirectory = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
if (Test-Path -LiteralPath $resolvedArtifactsDirectory) {
    throw "The non-UI release-gate artifact target already exists: $resolvedArtifactsDirectory"
}

New-Item -ItemType Directory -Path $resolvedArtifactsDirectory | Out-Null
$resultsDirectory = Join-Path $resolvedArtifactsDirectory "test-results"
$workerDirectory = Join-Path $resolvedArtifactsDirectory "worker-win-x64"
New-Item -ItemType Directory -Path $resultsDirectory | Out-Null
New-Item -ItemType Directory -Path $workerDirectory | Out-Null

$env:MIDORA_BASS_NATIVE_DIR = $resolvedNativeDirectory
$env:MIDORA_TEST_SOUNDFONT_PATH = $resolvedSoundFontPath

$solutions = @(
    "src/midora-common/midora-common.slnx",
    "src/midora-midi/midora-midi.slnx",
    "src/midora-native-interops/midora-native-interops.slnx",
    "src/midora-audio-device/midora-audio-device.slnx",
    "src/midora-core/midora-core.slnx",
    "src/midora-audio/midora-audio.slnx"
)
foreach ($solution in $solutions) {
    Invoke-DotNet -Arguments @(
        "restore",
        (Join-Path $repositoryRoot $solution),
        "--locked-mode")
}
foreach ($solution in $solutions) {
    Invoke-DotNet -Arguments @(
        "build",
        (Join-Path $repositoryRoot $solution),
        "-c", [string]$baseline.configuration,
        "--no-restore",
        "--verbosity", "minimal",
        "-p:ContinuousIntegrationBuild=true")
}

$workerProject = Join-Path `
    $repositoryRoot `
    "src\midora-audio\Midora.Audio.Bass.Worker\Midora.Audio.Bass.Worker.csproj"
Invoke-DotNet -Arguments @(
    "restore", $workerProject,
    "-r", "win-x64",
    "--locked-mode")
Invoke-DotNet -Arguments @(
    "publish", $workerProject,
    "-c", [string]$baseline.configuration,
    "-r", "win-x64",
    "--no-restore",
    "--output", $workerDirectory,
    "-p:ContinuousIntegrationBuild=true",
    "-p:BassNativeDirectory=$resolvedNativeDirectory")

$workerPath = Join-Path $workerDirectory "Midora.Audio.Bass.Worker.exe"
foreach ($requiredPath in @(
    $workerPath,
    (Join-Path $workerDirectory "native-manifest.json"),
    (Join-Path $workerDirectory "LICENSE"),
    (Join-Path $workerDirectory "THIRD-PARTY-NOTICES.md"))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The Native AOT Worker publish is missing a required artifact: $requiredPath"
    }
}
$env:MIDORA_TEST_NATIVE_AOT_FILE_WORKER = $workerPath
$env:MIDORA_TEST_NATIVE_AOT_REALTIME_WORKER = $workerPath

$observedTotal = 0
foreach ($project in $baseline.projects) {
    $projectPath = Join-Path $repositoryRoot ([string]$project.path)
    $resultName = ([System.IO.Path]::GetFileNameWithoutExtension($projectPath)) + ".trx"
    Invoke-DotNet -Arguments @(
        "test", $projectPath,
        "-c", [string]$baseline.configuration,
        "--no-build",
        "--no-restore",
        "--results-directory", $resultsDirectory,
        "--logger", "trx;LogFileName=$resultName",
        "--verbosity", "minimal")

    $resultPath = Join-Path $resultsDirectory $resultName
    [xml]$result = Get-Content -LiteralPath $resultPath -Raw -Encoding utf8
    $counters = $result.TestRun.ResultSummary.Counters
    $executed = [int]$counters.executed
    $passed = [int]$counters.passed
    $failed = [int]$counters.failed
    $notExecuted = [int]$counters.notExecuted
    $expected = [int]$project.expectedTestCount
    if ($executed -ne $expected -or $passed -ne $expected -or $failed -ne 0 -or $notExecuted -ne 0) {
        throw "Non-UI test baseline mismatch for $($project.path): expected=$expected, executed=$executed, passed=$passed, failed=$failed, notExecuted=$notExecuted."
    }
    $observedTotal += $executed
}
if ($observedTotal -ne [int]$baseline.totalTestCount) {
    throw "Non-UI total test baseline mismatch: expected=$($baseline.totalTestCount), actual=$observedTotal."
}

Write-Host "Non-UI release gate passed: $observedTotal tests, no skips, win-x64 Native AOT Worker at $workerDirectory."
Write-Host "BASS redistribution authorization remains a separate release-owner legal gate."
