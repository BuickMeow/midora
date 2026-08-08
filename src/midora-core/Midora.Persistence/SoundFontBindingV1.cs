using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

public enum ExternalSoundFontResolutionKind
{
    Exact,
    CaseInsensitiveFallback,
    Missing,
    Ambiguous,
    Unreadable
}

public sealed record SoundFontContentIdentityV1(
    string OriginalFileName,
    string Sha256,
    long FileSizeBytes);

public sealed record ExternalSoundFontBindingV1(
    ExternalProjectSoundFontReference Reference,
    string ResolvedAbsolutePath,
    bool UsedCaseInsensitiveFallback);

public sealed record ExternalSoundFontVerificationV1(
    ExternalSoundFontResolutionKind Resolution,
    string? ResolvedAbsolutePath,
    string? CurrentSha256,
    long? CurrentFileSizeBytes,
    bool? HashMatches)
{
    public ExternalSoundFontFileStampV1? FileStamp { get; init; }

    public bool IsReadable => Resolution is
        ExternalSoundFontResolutionKind.Exact or
        ExternalSoundFontResolutionKind.CaseInsensitiveFallback;

    public bool RequiresWarning =>
        Resolution == ExternalSoundFontResolutionKind.CaseInsensitiveFallback
        || HashMatches == false;
}

public static class SoundFontBindingV1
{
    public static Task<EmbeddedSoundFontImportCandidateV1> StageEmbeddedAsync(
        string selectedSoundFontPath,
        CancellationToken cancellationToken = default) =>
        EmbeddedSoundFontImportCandidateV1.CreateAsync(
            selectedSoundFontPath,
            cancellationToken);

    public static Task<EmbeddedSoundFontResourceV1> BindEmbeddedAsync(
        MidoraProject project,
        string selectedSoundFontPath,
        CancellationToken cancellationToken = default) =>
        EmbeddedSoundFontResourceV1.ImportAndBindAsync(
            project,
            selectedSoundFontPath,
            cancellationToken);

    public static async Task<ExternalSoundFontBindingV1> BindExternalAsync(
        string projectFilePath,
        string selectedSoundFontPath,
        CancellationToken cancellationToken = default)
    {
        string projectDirectory = GetProjectDirectory(projectFilePath);
        string selectedPath = GetFullyQualifiedPath(selectedSoundFontPath, nameof(selectedSoundFontPath));
        string relativePath = Path.GetRelativePath(projectDirectory, selectedPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        try
        {
            SoundFontReferenceValidation.ValidateExternalRelativePath(relativePath);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The selected external SoundFont is outside the two allowed Project-relative locations.",
                exception);
        }

        PathResolution resolution = ResolveRelativePath(projectDirectory, relativePath);
        if (resolution.Kind is not ExternalSoundFontResolutionKind.Exact
            and not ExternalSoundFontResolutionKind.CaseInsensitiveFallback)
        {
            throw new InvalidDataException($"The selected external SoundFont is {resolution.Kind}.");
        }

        SoundFontContentIdentityV1 identity = await ReadContentIdentityAsync(
            resolution.AbsolutePath!, cancellationToken).ConfigureAwait(false);
        string actualRelativePath = Path.GetRelativePath(projectDirectory, resolution.AbsolutePath!)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        ExternalProjectSoundFontReference reference = new(
            actualRelativePath,
            identity.OriginalFileName,
            identity.Sha256,
            identity.FileSizeBytes);
        return new(
            reference,
            resolution.AbsolutePath!,
            resolution.Kind == ExternalSoundFontResolutionKind.CaseInsensitiveFallback);
    }

    public static async Task<ExternalSoundFontVerificationV1> VerifyExternalAsync(
        string projectFilePath,
        ExternalProjectSoundFontReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ExternalSoundFontResolutionV1 resolution = ResolveExternal(
            projectFilePath,
            reference);
        if (resolution.Resolution is not ExternalSoundFontResolutionKind.Exact
            and not ExternalSoundFontResolutionKind.CaseInsensitiveFallback)
        {
            return new(resolution.Resolution, null, null, null, null);
        }

        try
        {
            SoundFontFileSnapshotV1 current = await SoundFontFileSnapshotReaderV1.ReadAsync(
                resolution.ResolvedAbsolutePath!,
                cancellationToken).ConfigureAwait(false);
            bool matches = current.Identity.FileSizeBytes == reference.FileSizeBytes
                && string.Equals(
                    current.Identity.Sha256,
                    reference.Sha256,
                    StringComparison.Ordinal);
            return new(
                resolution.Resolution,
                resolution.ResolvedAbsolutePath,
                current.Identity.Sha256,
                current.Identity.FileSizeBytes,
                matches)
            {
                FileStamp = current.Stamp
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            return new(ExternalSoundFontResolutionKind.Unreadable, null, null, null, null);
        }
    }

    public static async Task<SoundFontContentIdentityV1> ReadContentIdentityAsync(
        string soundFontPath,
        CancellationToken cancellationToken = default)
    {
        string path = GetFullyQualifiedPath(soundFontPath, nameof(soundFontPath));
        SoundFontFileSnapshotV1 snapshot = await SoundFontFileSnapshotReaderV1.ReadAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        return snapshot.Identity;
    }

    public static ExternalSoundFontResolutionV1 ResolveExternal(
        string projectFilePath,
        ExternalProjectSoundFontReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string projectDirectory = GetProjectDirectory(projectFilePath);
        PathResolution resolution = ResolveRelativePath(projectDirectory, reference.RelativePath);
        return new(resolution.Kind, resolution.AbsolutePath);
    }

    internal static (string? Name, bool UsedFallback, bool Ambiguous) SelectCandidateName(
        IReadOnlyList<string> candidateNames,
        string requestedName)
    {
        string? exact = candidateNames.FirstOrDefault(candidate =>
            string.Equals(candidate, requestedName, StringComparison.Ordinal));
        if (exact is not null)
        {
            return (exact, false, false);
        }
        string[] insensitive = candidateNames
            .Where(candidate => string.Equals(candidate, requestedName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return insensitive.Length switch
        {
            0 => (null, false, false),
            1 => (insensitive[0], true, false),
            _ => (null, false, true)
        };
    }

    private static PathResolution ResolveRelativePath(string projectDirectory, string relativePath)
    {
        string[] segments = relativePath.Split('/');
        try
        {
            string parent = projectDirectory;
            bool usedFallback = false;
            if (segments.Length == 2)
            {
                PathResolution directory = ResolveChild(parent, segments[0], directories: true);
                if (directory.Kind is not ExternalSoundFontResolutionKind.Exact
                    and not ExternalSoundFontResolutionKind.CaseInsensitiveFallback)
                {
                    return directory;
                }
                parent = directory.AbsolutePath!;
                usedFallback = directory.Kind == ExternalSoundFontResolutionKind.CaseInsensitiveFallback;
            }

            PathResolution file = ResolveChild(parent, segments[^1], directories: false);
            if (file.Kind is not ExternalSoundFontResolutionKind.Exact
                and not ExternalSoundFontResolutionKind.CaseInsensitiveFallback)
            {
                return file;
            }
            return new(
                usedFallback || file.Kind == ExternalSoundFontResolutionKind.CaseInsensitiveFallback
                    ? ExternalSoundFontResolutionKind.CaseInsensitiveFallback
                    : ExternalSoundFontResolutionKind.Exact,
                file.AbsolutePath);
        }
        catch (UnauthorizedAccessException)
        {
            return new(ExternalSoundFontResolutionKind.Unreadable, null);
        }
        catch (IOException)
        {
            return new(ExternalSoundFontResolutionKind.Unreadable, null);
        }
    }

    private static PathResolution ResolveChild(string parent, string requestedName, bool directories)
    {
        if (!Directory.Exists(parent))
        {
            return new(ExternalSoundFontResolutionKind.Missing, null);
        }
        string[] paths = (directories
                ? Directory.EnumerateDirectories(parent)
                : Directory.EnumerateFiles(parent))
            .ToArray();
        string[] names = paths
            .Select(path => Path.GetFileName(path)
                ?? throw new InvalidDataException("A directory entry has no file name."))
            .ToArray();
        (string? name, bool fallback, bool ambiguous) = SelectCandidateName(names, requestedName);
        if (ambiguous)
        {
            return new(ExternalSoundFontResolutionKind.Ambiguous, null);
        }
        if (name is null)
        {
            return new(ExternalSoundFontResolutionKind.Missing, null);
        }
        string resolved = paths.Single(path =>
            string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal));
        return new(
            fallback
                ? ExternalSoundFontResolutionKind.CaseInsensitiveFallback
                : ExternalSoundFontResolutionKind.Exact,
            resolved);
    }

    private static string GetProjectDirectory(string projectFilePath)
    {
        string path = GetFullyQualifiedPath(projectFilePath, nameof(projectFilePath));
        return Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Project file path has no parent directory.", nameof(projectFilePath));
    }

    private static string GetFullyQualifiedPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Path must be fully qualified.", parameterName);
        }
        return Path.GetFullPath(value);
    }

    private sealed record PathResolution(
        ExternalSoundFontResolutionKind Kind,
        string? AbsolutePath);
}
