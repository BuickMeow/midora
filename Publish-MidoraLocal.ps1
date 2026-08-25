[CmdletBinding()]
param(
    [string]$BassNativeDirectory,
    [ValidateSet("Release")]
    [string]$Configuration = "Release"
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

function Resolve-BassNativeDirectory {
    param([string]$RequestedDirectory)

    if (-not [string]::IsNullOrWhiteSpace($RequestedDirectory)) {
        return [System.IO.Path]::GetFullPath($RequestedDirectory)
    }

    $configuredDirectory = [Environment]::GetEnvironmentVariable(
        "MIDORA_BASS_NATIVE_DIR",
        "Process")
    if (-not [string]::IsNullOrWhiteSpace($configuredDirectory)) {
        return [System.IO.Path]::GetFullPath($configuredDirectory)
    }

    $localApplicationData =
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localApplicationData)) {
        throw "Unable to resolve LocalApplicationData. Pass -BassNativeDirectory explicitly."
    }

    return [System.IO.Path]::GetFullPath(
        (Join-Path $localApplicationData "Midora\Native\BASS\win-x64"))
}

function Assert-ExactReleasePath {
    param(
        [Parameter(Mandatory)] [string]$ActualPath,
        [Parameter(Mandatory)] [string]$ExpectedPath,
        [Parameter(Mandatory)] [string]$Description
    )

    $actual = [System.IO.Path]::GetFullPath($ActualPath).TrimEnd('\', '/')
    $expected = [System.IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\', '/')
    if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description resolved outside its fixed release target. Expected=$expected; Actual=$actual"
    }

    if (Test-Path -LiteralPath $actual) {
        $item = Get-Item -LiteralPath $actual -Force
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description must not be a reparse point: $actual"
        }
    }
}

function Move-CurrentArchivesToHistory {
    param(
        [Parameter(Mandatory)] [string]$DistDirectory,
        [Parameter(Mandatory)] [string]$HistoryDirectory
    )

    $archives = @(Get-ChildItem -LiteralPath $DistDirectory -File -Filter "midora-*.zip")
    foreach ($archive in $archives) {
        $destination = Join-Path $HistoryDirectory $archive.Name
        if (Test-Path -LiteralPath $destination) {
            $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ")
            $destination = Join-Path `
                $HistoryDirectory `
                ("{0}-archived-{1}{2}" -f $archive.BaseName, $timestamp, $archive.Extension)
        }

        Move-Item -LiteralPath $archive.FullName -Destination $destination

        $hashSidecar = $archive.FullName + ".sha256"
        if (Test-Path -LiteralPath $hashSidecar -PathType Leaf) {
            $sidecarDestination = $destination + ".sha256"
            Move-Item -LiteralPath $hashSidecar -Destination $sidecarDestination
        }
    }
}

function Get-ExactDownloadDependencyVersion {
    param(
        [Parameter(Mandatory)] [object]$Assets,
        [Parameter(Mandatory)] [string]$PackageId
    )

    $framework = @($Assets.project.frameworks.PSObject.Properties)[0].Value
    $dependency = @($framework.downloadDependencies) |
        Where-Object { $_.name -eq $PackageId } |
        Select-Object -First 1
    if ($null -eq $dependency) {
        throw "The locked restore did not resolve required runtime pack $PackageId."
    }

    $range = [string]$dependency.version
    $match = [regex]::Match($range, '^\[([^,]+),\s*([^\]]+)\]$')
    if (-not $match.Success -or $match.Groups[1].Value -ne $match.Groups[2].Value) {
        throw "Runtime pack $PackageId is not pinned to one exact version: $range"
    }

    return $match.Groups[1].Value
}

function Get-ExactLibraryVersion {
    param(
        [Parameter(Mandatory)] [object]$Assets,
        [Parameter(Mandatory)] [string]$PackageId
    )

    $prefix = "$PackageId/"
    $entry = @($Assets.libraries.PSObject.Properties.Name) |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($entry)) {
        throw "The locked restore did not resolve required package $PackageId."
    }

    return $entry.Substring($prefix.Length)
}

function Copy-RequiredNotice {
    param(
        [Parameter(Mandatory)] [string]$SourcePath,
        [Parameter(Mandatory)] [string]$DestinationPath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "A required third-party notice is absent from the resolved package: $SourcePath"
    }
    $parent = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force
}

$repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$distDirectory = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot "dist"))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $distDirectory "midora"))
$historyDirectory = [System.IO.Path]::GetFullPath((Join-Path $distDirectory "history"))

Assert-ExactReleasePath `
    -ActualPath $distDirectory `
    -ExpectedPath (Join-Path $repositoryRoot "dist") `
    -Description "Distribution root"
Assert-ExactReleasePath `
    -ActualPath $publishDirectory `
    -ExpectedPath (Join-Path $repositoryRoot "dist\midora") `
    -Description "Current publish directory"
Assert-ExactReleasePath `
    -ActualPath $historyDirectory `
    -ExpectedPath (Join-Path $repositoryRoot "dist\history") `
    -Description "Release history directory"

$desktopProject = Join-Path `
    $repositoryRoot `
    "src\midora-desktop\Midora.Desktop\Midora.Desktop.csproj"
$workerProject = Join-Path `
    $repositoryRoot `
    "src\midora-audio\Midora.Audio.Bass.Worker\Midora.Audio.Bass.Worker.csproj"
$nativeValidator = Join-Path $repositoryRoot "src\midora-audio\Test-BassNative.ps1"
$versionPropsPath = Join-Path $repositoryRoot "eng\Version.props"

[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw -Encoding utf8
$versionPrefix = [string]$versionProps.Project.PropertyGroup.VersionPrefix
$versionSuffix = [string]$versionProps.Project.PropertyGroup.VersionSuffix
$productVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
} else {
    "$versionPrefix-$versionSuffix"
}

& (Join-Path $repositoryRoot "Test-VersionControl.ps1") -ExpectedVersion $productVersion

$commitId = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commitId -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Unable to resolve the Git commit used for this local release."
}
$shortCommitId = $commitId.Substring(0, 8).ToLowerInvariant()
$gitStatus = @(& git -C $repositoryRoot status --porcelain)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect the Git worktree state."
}
$isDirty = $gitStatus.Count -gt 0
$dirtySuffix = if ($isDirty) { "-dirty" } else { "" }
$archiveBaseName = "midora-$productVersion-win-x64-$shortCommitId$dirtySuffix"
$archivePath = Join-Path $distDirectory "$archiveBaseName.zip"
$hashPath = $archivePath + ".sha256"

$resolvedNativeDirectory = Resolve-BassNativeDirectory `
    -RequestedDirectory $BassNativeDirectory
if (-not (Test-Path -LiteralPath $resolvedNativeDirectory -PathType Container)) {
    throw "The pinned win-x64 BASS baseline directory was not found: $resolvedNativeDirectory"
}
& $nativeValidator -Directory $resolvedNativeDirectory -Architecture win-x64

New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $historyDirectory -Force | Out-Null
Move-CurrentArchivesToHistory `
    -DistDirectory $distDirectory `
    -HistoryDirectory $historyDirectory

if (Test-Path -LiteralPath $publishDirectory) {
    Assert-ExactReleasePath `
        -ActualPath $publishDirectory `
        -ExpectedPath (Join-Path $repositoryRoot "dist\midora") `
        -Description "Current publish directory"
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory | Out-Null

Write-Host "Restoring locked dependencies..."
Invoke-DotNet -Arguments @(
    "restore", $desktopProject,
    "-r", "win-x64",
    "--locked-mode")
Invoke-DotNet -Arguments @(
    "restore", $workerProject,
    "-r", "win-x64",
    "--locked-mode")

$desktopAssetsPath = Join-Path `
    (Split-Path -Parent $desktopProject) `
    "obj\project.assets.json"
$desktopAssets = Get-Content -LiteralPath $desktopAssetsPath -Raw -Encoding utf8 |
    ConvertFrom-Json
$packageRoot = @($desktopAssets.packageFolders.PSObject.Properties.Name)[0]
if ([string]::IsNullOrWhiteSpace($packageRoot)) {
    throw "The locked restore did not report a NuGet global package directory."
}
$netCoreVersion = Get-ExactDownloadDependencyVersion `
    -Assets $desktopAssets `
    -PackageId "Microsoft.NETCore.App.Runtime.win-x64"
$aspNetCoreVersion = Get-ExactDownloadDependencyVersion `
    -Assets $desktopAssets `
    -PackageId "Microsoft.AspNetCore.App.Runtime.win-x64"
$windowsDesktopVersion = Get-ExactDownloadDependencyVersion `
    -Assets $desktopAssets `
    -PackageId "Microsoft.WindowsDesktop.App.Runtime.win-x64"
$roslynVersion = Get-ExactLibraryVersion `
    -Assets $desktopAssets `
    -PackageId "Microsoft.CodeAnalysis.CSharp"

Write-Host "Publishing self-contained single-file Midora..."
Invoke-DotNet -Arguments @(
    "publish", $desktopProject,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "--output", $publishDirectory,
    "-p:PublishSingleFile=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:PublishTrimmed=false",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:ContinuousIntegrationBuild=true")

$dotNetLicenseDirectory = Join-Path $publishDirectory "licenses\dotnet"
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.netcore.app.runtime.win-x64\$netCoreVersion\LICENSE.TXT") `
    -DestinationPath (Join-Path $dotNetLicenseDirectory "Microsoft.NETCore.App-LICENSE.txt")
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.netcore.app.runtime.win-x64\$netCoreVersion\THIRD-PARTY-NOTICES.TXT") `
    -DestinationPath (Join-Path $dotNetLicenseDirectory "Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt")
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.aspnetcore.app.runtime.win-x64\$aspNetCoreVersion\LICENSE.txt") `
    -DestinationPath (Join-Path $dotNetLicenseDirectory "Microsoft.AspNetCore.App-LICENSE.txt")
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.aspnetcore.app.runtime.win-x64\$aspNetCoreVersion\THIRD-PARTY-NOTICES.TXT") `
    -DestinationPath (Join-Path $dotNetLicenseDirectory "Microsoft.AspNetCore.App-THIRD-PARTY-NOTICES.txt")
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.windowsdesktop.app.runtime.win-x64\$windowsDesktopVersion\LICENSE") `
    -DestinationPath (Join-Path $dotNetLicenseDirectory "Microsoft.WindowsDesktop.App-LICENSE.txt")
Copy-RequiredNotice `
    -SourcePath (Join-Path `
        $packageRoot `
        "microsoft.codeanalysis.csharp\$roslynVersion\ThirdPartyNotices.rtf") `
    -DestinationPath (Join-Path $publishDirectory "licenses\Roslyn-ThirdPartyNotices.rtf")

$workerDirectory = Join-Path $publishDirectory "audio-worker"
New-Item -ItemType Directory -Path $workerDirectory -Force | Out-Null
Write-Host "Publishing self-contained Native AOT audio worker..."
Invoke-DotNet -Arguments @(
    "publish", $workerProject,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "--output", $workerDirectory,
    "-p:BassNativeDirectory=$resolvedNativeDirectory",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:ContinuousIntegrationBuild=true")

Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter "*.pdb" |
    Remove-Item -Force

$requiredFiles = @(
    (Join-Path $publishDirectory "Midora.exe"),
    (Join-Path $publishDirectory "LICENSE"),
    (Join-Path $publishDirectory "THIRD-PARTY-NOTICES.md"),
    (Join-Path $publishDirectory "licenses\Roslyn-ThirdPartyNotices.rtf"),
    (Join-Path $dotNetLicenseDirectory "Microsoft.NETCore.App-LICENSE.txt"),
    (Join-Path $dotNetLicenseDirectory "Microsoft.NETCore.App-THIRD-PARTY-NOTICES.txt"),
    (Join-Path $dotNetLicenseDirectory "Microsoft.AspNetCore.App-LICENSE.txt"),
    (Join-Path $dotNetLicenseDirectory "Microsoft.AspNetCore.App-THIRD-PARTY-NOTICES.txt"),
    (Join-Path $dotNetLicenseDirectory "Microsoft.WindowsDesktop.App-LICENSE.txt"),
    (Join-Path $workerDirectory "Midora.Audio.Bass.Worker.exe"),
    (Join-Path $workerDirectory "bass.dll"),
    (Join-Path $workerDirectory "bassmidi.dll"),
    (Join-Path $workerDirectory "basswasapi.dll"),
    (Join-Path $workerDirectory "native-manifest.json")
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "The local release is incomplete. Missing: $requiredFile"
    }
}

$pdbFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter "*.pdb")
if ($pdbFiles.Count -ne 0) {
    throw "The local release contains PDB files."
}

$forbiddenPublishPaths = @(
    (Join-Path $publishDirectory "Schemas"),
    (Join-Path $publishDirectory "release-manifest.json")
)
foreach ($forbiddenPath in $forbiddenPublishPaths) {
    if (Test-Path -LiteralPath $forbiddenPath) {
        throw "The end-user release contains an internal-only artifact: $forbiddenPath"
    }
}

if (Test-Path -LiteralPath $archivePath) {
    throw "The current archive path unexpectedly already exists: $archivePath"
}
Write-Host "Creating archive with a single midora root directory..."
Compress-Archive `
    -LiteralPath $publishDirectory `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($zip.Entries)
    if ($entries.Count -eq 0) {
        throw "The generated release archive is empty."
    }
    $invalidEntries = @($entries | Where-Object {
        -not $_.FullName.StartsWith("midora/", [StringComparison]::OrdinalIgnoreCase)
    })
    if ($invalidEntries.Count -ne 0) {
        throw "The release archive does not contain exactly one midora root directory."
    }
} finally {
    $zip.Dispose()
}

$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $([System.IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $hashPath -Encoding ascii

$archiveSizeMiB = [Math]::Round(
    (Get-Item -LiteralPath $archivePath).Length / 1MB,
    2)
Write-Host "Local Midora release completed."
Write-Host "Directory: $publishDirectory"
Write-Host "Archive:   $archivePath ($archiveSizeMiB MiB)"
Write-Host "SHA-256:   $archiveHash"
if ($isDirty) {
    Write-Warning "The package includes uncommitted or untracked changes and is marked -dirty."
}
