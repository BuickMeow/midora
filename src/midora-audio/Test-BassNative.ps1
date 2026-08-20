[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,
    [Parameter(Mandatory)]
    [ValidateSet("win-x64")]
    [string]$Architecture,
    [switch]$AllowUnpinnedDevelopmentCandidate
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $stream = [System.IO.File]::OpenRead($LiteralPath)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $algorithm.ComputeHash($stream)
        }
        finally {
            $algorithm.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
}

$resolvedDirectory = [System.IO.Path]::GetFullPath($Directory)
$installedManifestPath = Join-Path $resolvedDirectory "native-manifest.json"
$baselineManifestPath = Join-Path $PSScriptRoot "bass-native-baseline.win-x64.json"

if ($AllowUnpinnedDevelopmentCandidate) {
    if (-not (Test-Path -LiteralPath $installedManifestPath -PathType Leaf)) {
        throw "BASS native candidate manifest does not exist: $installedManifestPath"
    }

    $manifest = Get-Content -LiteralPath $installedManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.releaseBaseline -ne $false) {
        throw "An unpinned development candidate manifest must use schemaVersion=1 and declare releaseBaseline=false."
    }
}
else {
    if (-not (Test-Path -LiteralPath $baselineManifestPath -PathType Leaf)) {
        throw "Pinned BASS release baseline does not exist: $baselineManifestPath"
    }

    $manifest = Get-Content -LiteralPath $baselineManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.releaseBaseline -ne $true) {
        throw "Pinned BASS release baseline has an unsupported schema or is not marked as a release baseline."
    }
}

if ($manifest.architecture -ne $Architecture) {
    throw "BASS native architecture mismatch: manifest=$($manifest.architecture), requested=$Architecture."
}

$expectedNames = @("bass.dll", "bassmidi.dll", "basswasapi.dll")
$manifestNames = @($manifest.files | ForEach-Object { [string]$_.name })
if ((@($manifestNames | Sort-Object) -join "`n") -ne (@($expectedNames | Sort-Object) -join "`n")) {
    throw "BASS native manifest must contain exactly bass.dll, bassmidi.dll, and basswasapi.dll."
}

$actualDllNames = @(
    Get-ChildItem -LiteralPath $resolvedDirectory -File -Filter "*.dll" |
        ForEach-Object { $_.Name }
)
if ((@($actualDllNames | Sort-Object) -join "`n") -ne (@($expectedNames | Sort-Object) -join "`n")) {
    throw "BASS native directory must contain exactly bass.dll, bassmidi.dll, and basswasapi.dll."
}

foreach ($entry in $manifest.files) {
    $fileName = [string]$entry.name
    if (-not $AllowUnpinnedDevelopmentCandidate) {
        $hasValidVersion = ([string]$entry.version) -match "^\d+\.\d+\.\d+\.\d+$"
        $hasValidVersionCode = ([string]$entry.versionCode) -match "^0x[0-9a-fA-F]{8}$"
        if (-not $hasValidVersion -or -not $hasValidVersionCode) {
            throw "Pinned BASS release baseline has an invalid version or versionCode for $fileName."
        }
    }

    $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
    if ($expectedHash -notmatch "^[0-9a-f]{64}$") {
        throw "Invalid SHA-256 in BASS native manifest for $fileName."
    }

    $filePath = Join-Path $resolvedDirectory $fileName
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "BASS native file does not exist: $filePath"
    }

    $actualHash = Get-Sha256Hex -LiteralPath $filePath
    if ($actualHash -ne $expectedHash) {
        throw "BASS native SHA-256 mismatch for $fileName."
    }
}

if ($AllowUnpinnedDevelopmentCandidate) {
    Write-Host "Validated unpinned BASS native development candidate at $resolvedDirectory ($Architecture)."
}
else {
    Write-Host "Validated pinned BASS native release baseline at $resolvedDirectory ($Architecture)."
}
