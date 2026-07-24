[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$Force,
    [switch]$SetUserEnvironmentVariable
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$environmentVariableName = "MIDORA_BASS_NATIVE_DIR"
$bassUrl = "https://www.un4seen.com/files/bass24.zip"
$bassMidiUrl = "https://www.un4seen.com/files/bassmidi24.zip"
$bassWasapiUrl = "https://www.un4seen.com/files/basswasapi24.zip"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("midora-bass-" + [Guid]::NewGuid().ToString("N"))

function Resolve-Destination {
    param([string]$RequestedDestination)

    if (-not [string]::IsNullOrWhiteSpace($RequestedDestination)) {
        return [System.IO.Path]::GetFullPath($RequestedDestination)
    }

    $configured = [Environment]::GetEnvironmentVariable($environmentVariableName, "Process")
    if (-not [string]::IsNullOrWhiteSpace($configured)) {
        return [System.IO.Path]::GetFullPath($configured)
    }

    $localApplicationData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw "Unable to resolve LocalApplicationData. Pass -Destination or set $environmentVariableName."
    }

    return Join-Path $localApplicationData "Midora\Native\BASS\win-x64"
}

function Get-X64Dll {
    param(
        [Parameter(Mandatory)] [string]$ExtractedRoot,
        [Parameter(Mandatory)] [string]$FileName
    )

    $candidates = @(
        Get-ChildItem -Path $ExtractedRoot -Recurse -File -Filter $FileName |
            Where-Object { $_.FullName -match "[\\/]x64[\\/]" }
    )

    if ($candidates.Count -ne 1) {
        throw "Expected exactly one x64/$FileName in the official package; found $($candidates.Count)."
    }

    return [string]$candidates[0].FullName
}

function Test-InstalledFiles {
    param([Parameter(Mandatory)] [string]$Directory)

    foreach ($fileName in "bass.dll", "bassmidi.dll", "basswasapi.dll") {
        if (-not (Test-Path (Join-Path $Directory $fileName) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

$resolvedDestination = Resolve-Destination -RequestedDestination $Destination

if ((Test-InstalledFiles -Directory $resolvedDestination) -and -not $Force) {
    try {
        & (Join-Path $PSScriptRoot "Test-BassNative.ps1") -Directory $resolvedDestination
    }
    catch {
        throw "Existing BASS native installation failed validation. Use -Force only if you intend to replace it. $($_.Exception.Message)"
    }

    Write-Host "BASS native x64 files already exist at: $resolvedDestination"
    Write-Host "No network request was made. Use -Force only when you intentionally want to refresh them."

    if ($SetUserEnvironmentVariable) {
        [Environment]::SetEnvironmentVariable($environmentVariableName, $resolvedDestination, "Process")
        [Environment]::SetEnvironmentVariable($environmentVariableName, $resolvedDestination, "User")
        Write-Host "Set process and user environment variable $environmentVariableName=$resolvedDestination"
    }

    return
}

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    New-Item -ItemType Directory -Path $resolvedDestination -Force | Out-Null

    $bassZip = Join-Path $tempRoot "bass24.zip"
    $midiZip = Join-Path $tempRoot "bassmidi24.zip"
    $wasapiZip = Join-Path $tempRoot "basswasapi24.zip"
    Invoke-WebRequest -Uri $bassUrl -OutFile $bassZip
    Invoke-WebRequest -Uri $bassMidiUrl -OutFile $midiZip
    Invoke-WebRequest -Uri $bassWasapiUrl -OutFile $wasapiZip

    $bassDir = Join-Path $tempRoot "bass"
    $midiDir = Join-Path $tempRoot "bassmidi"
    $wasapiDir = Join-Path $tempRoot "basswasapi"
    Expand-Archive -Path $bassZip -DestinationPath $bassDir
    Expand-Archive -Path $midiZip -DestinationPath $midiDir
    Expand-Archive -Path $wasapiZip -DestinationPath $wasapiDir

    $bassDll = Get-X64Dll -ExtractedRoot $bassDir -FileName "bass.dll"
    $bassMidiDll = Get-X64Dll -ExtractedRoot $midiDir -FileName "bassmidi.dll"
    $bassWasapiDll = Get-X64Dll -ExtractedRoot $wasapiDir -FileName "basswasapi.dll"

    Copy-Item $bassDll (Join-Path $resolvedDestination "bass.dll") -Force
    Copy-Item $bassMidiDll (Join-Path $resolvedDestination "bassmidi.dll") -Force
    Copy-Item $bassWasapiDll (Join-Path $resolvedDestination "basswasapi.dll") -Force

    $manifest = [ordered]@{
        retrievedAtUtc = [DateTime]::UtcNow.ToString("O")
        source = @($bassUrl, $bassMidiUrl, $bassWasapiUrl)
        files = @(
            [ordered]@{
                name = "bass.dll"
                sha256 = (Get-FileHash (Join-Path $resolvedDestination "bass.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
            },
            [ordered]@{
                name = "bassmidi.dll"
                sha256 = (Get-FileHash (Join-Path $resolvedDestination "bassmidi.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
            },
            [ordered]@{
                name = "basswasapi.dll"
                sha256 = (Get-FileHash (Join-Path $resolvedDestination "basswasapi.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        )
    }

    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $resolvedDestination "native-manifest.json") -Encoding utf8

    if ($SetUserEnvironmentVariable) {
        [Environment]::SetEnvironmentVariable($environmentVariableName, $resolvedDestination, "Process")
        [Environment]::SetEnvironmentVariable($environmentVariableName, $resolvedDestination, "User")
        Write-Host "Set process and user environment variable $environmentVariableName=$resolvedDestination"
    }

    Write-Host "BASS native x64 files installed at: $resolvedDestination"
}
finally {
    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force
    }
}
