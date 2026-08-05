[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory,
    [Parameter(Mandatory)]
    [ValidateSet("win-x64", "win-x86")]
    [string]$Architecture
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedDirectory = [System.IO.Path]::GetFullPath($Directory)
$manifestPath = Join-Path $resolvedDirectory "native-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "BASS native manifest does not exist: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($manifest.architecture -ne $Architecture) {
    throw "BASS native architecture mismatch: manifest=$($manifest.architecture), requested=$Architecture."
}

$expectedNames = @("bass.dll", "bassmidi.dll", "basswasapi.dll")
$manifestNames = @($manifest.files | ForEach-Object { [string]$_.name })
if ((@($manifestNames | Sort-Object) -join "`n") -ne (@($expectedNames | Sort-Object) -join "`n")) {
    throw "BASS native manifest must contain exactly bass.dll, bassmidi.dll, and basswasapi.dll."
}

foreach ($entry in $manifest.files) {
    $fileName = [string]$entry.name
    $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
    if ($expectedHash -notmatch "^[0-9a-f]{64}$") {
        throw "Invalid SHA-256 in BASS native manifest for $fileName."
    }

    $filePath = Join-Path $resolvedDirectory $fileName
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "BASS native file does not exist: $filePath"
    }

    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "BASS native SHA-256 mismatch for $fileName."
    }
}

Write-Host "Validated BASS native candidate at $resolvedDirectory ($Architecture)."
