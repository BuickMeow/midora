[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$BassNativeDirectory,
    [switch]$PrepareOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = $PSScriptRoot
$desktopProject = Join-Path $repositoryRoot "src\midora-desktop\Midora.Desktop\Midora.Desktop.csproj"
$workerProject = Join-Path $repositoryRoot "src\midora-audio\Midora.Audio.Bass.Worker\Midora.Audio.Bass.Worker.csproj"
$nativeValidator = Join-Path $repositoryRoot "src\midora-audio\Test-BassNative.ps1"

function Resolve-BassNativeDirectory {
    param([string]$RequestedDirectory)

    if (-not [string]::IsNullOrWhiteSpace($RequestedDirectory)) {
        return [System.IO.Path]::GetFullPath($RequestedDirectory)
    }

    $configuredDirectory = [Environment]::GetEnvironmentVariable("MIDORA_BASS_NATIVE_DIR", "Process")
    if (-not [string]::IsNullOrWhiteSpace($configuredDirectory)) {
        return [System.IO.Path]::GetFullPath($configuredDirectory)
    }

    $localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw "Unable to resolve LocalApplicationData. Pass the installed pinned win-x64 BASS baseline through -BassNativeDirectory."
    }

    return Join-Path $localApplicationData "Midora\Native\BASS\win-x64"
}

$resolvedNativeDirectory = Resolve-BassNativeDirectory -RequestedDirectory $BassNativeDirectory
if (-not (Test-Path -LiteralPath $resolvedNativeDirectory -PathType Container)) {
    throw "The pinned win-x64 BASS baseline directory was not found: $resolvedNativeDirectory. Pass it explicitly through -BassNativeDirectory."
}

& $nativeValidator -Directory $resolvedNativeDirectory -Architecture win-x64

$desktopOutputDirectory = Join-Path $repositoryRoot "src\midora-desktop\Midora.Desktop\bin\$Configuration\net10.0-windows\win-x64"
$workerOutputDirectory = Join-Path $desktopOutputDirectory "audio-worker"
$publishArguments = @(
    "publish",
    $workerProject,
    "-c", "Release",
    "-r", "win-x64",
    "-p:BassNativeDirectory=$resolvedNativeDirectory",
    "-o", $workerOutputDirectory
)

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Native AOT audio worker publish failed with dotnet exit code $LASTEXITCODE."
}

$requiredWorkerFiles = @(
    "Midora.Audio.Bass.Worker.exe",
    "bass.dll",
    "bassmidi.dll",
    "basswasapi.dll",
    "native-manifest.json"
)
foreach ($fileName in $requiredWorkerFiles) {
    $filePath = Join-Path $workerOutputDirectory $fileName
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "The audio worker deployment is incomplete. Missing: $filePath"
    }
}

Write-Host "The formal audio worker was deployed to: $workerOutputDirectory"
if ($PrepareOnly) {
    return
}

& dotnet run --project $desktopProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Midora.Desktop exited with code $LASTEXITCODE."
}
