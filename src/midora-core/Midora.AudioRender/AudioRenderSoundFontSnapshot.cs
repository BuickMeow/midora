using System.Buffers;
using System.Security.Cryptography;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.AudioRender;

public enum AudioRenderSoundFontFailure
{
    NoReference,
    ProjectPathRequired,
    Missing,
    Ambiguous,
    Unreadable,
    EmbeddedResourceUnavailable,
    ContentChangedDuringFreeze,
    ExternalHashChangeRequiresConfirmation
}

public sealed class AudioRenderSoundFontException : Exception
{
    internal AudioRenderSoundFontException(
        AudioRenderSoundFontFailure failure,
        string message,
        string? currentSha256 = null,
        long? currentFileSizeBytes = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        CurrentSha256 = currentSha256;
        CurrentFileSizeBytes = currentFileSizeBytes;
    }

    public AudioRenderSoundFontFailure Failure { get; }
    public string? CurrentSha256 { get; }
    public long? CurrentFileSizeBytes { get; }
}

public sealed class AudioRenderSoundFontSnapshot : IDisposable, IAsyncDisposable
{
    private string? _ownedDirectory;

    private AudioRenderSoundFontSnapshot(
        ProjectSoundFontReference reference,
        string resolvedSourcePath,
        string frozenPath,
        string ownedDirectory,
        string sha256,
        long fileSizeBytes,
        AudioRenderDiagnostic[] diagnostics)
    {
        Reference = reference;
        ResolvedSourcePath = resolvedSourcePath;
        FrozenPath = frozenPath;
        _ownedDirectory = ownedDirectory;
        Sha256 = sha256;
        FileSizeBytes = fileSizeBytes;
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public ProjectSoundFontReference Reference { get; }
    public string ResolvedSourcePath { get; }
    public string FrozenPath { get; }
    public string Sha256 { get; }
    public long FileSizeBytes { get; }
    public IReadOnlyList<AudioRenderDiagnostic> Diagnostics { get; }

    public static async Task<AudioRenderSoundFontSnapshot> CreateAsync(
        MidoraProject project,
        string? currentProjectFilePath,
        EmbeddedSoundFontResourceV1? embeddedResource,
        bool acceptExternalHashChange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectSoundFontReference reference = project.SoundFont.Reference
            ?? throw new AudioRenderSoundFontException(
                AudioRenderSoundFontFailure.NoReference,
                "Audio rendering requires a Project SoundFont.");

        string sourcePath;
        List<AudioRenderDiagnostic> diagnostics = [];
        string? verifiedSha256 = null;
        long? verifiedSize = null;
        if (reference is ExternalProjectSoundFontReference external)
        {
            if (string.IsNullOrWhiteSpace(currentProjectFilePath))
            {
                throw new AudioRenderSoundFontException(
                    AudioRenderSoundFontFailure.ProjectPathRequired,
                    "An external Project SoundFont requires the current .midora path for deterministic resolution.");
            }

            ExternalSoundFontVerificationV1 verification = await SoundFontBindingV1.VerifyExternalAsync(
                currentProjectFilePath,
                external,
                cancellationToken).ConfigureAwait(false);
            if (!verification.IsReadable || verification.ResolvedAbsolutePath is null)
            {
                throw new AudioRenderSoundFontException(
                    verification.Resolution switch
                    {
                        ExternalSoundFontResolutionKind.Missing => AudioRenderSoundFontFailure.Missing,
                        ExternalSoundFontResolutionKind.Ambiguous => AudioRenderSoundFontFailure.Ambiguous,
                        _ => AudioRenderSoundFontFailure.Unreadable
                    },
                    $"The external Project SoundFont is {verification.Resolution}.");
            }
            sourcePath = verification.ResolvedAbsolutePath;
            verifiedSha256 = verification.CurrentSha256;
            verifiedSize = verification.CurrentFileSizeBytes;
            if (verification.Resolution == ExternalSoundFontResolutionKind.CaseInsensitiveFallback)
            {
                diagnostics.Add(new(
                    "MIDORA-AUDIO-RENDER-SF2-CASE-FALLBACK",
                    AudioRenderDiagnosticSeverity.Warning,
                    "The external SoundFont used the unique case-insensitive path fallback."));
            }
            if (verification.HashMatches == false && !acceptExternalHashChange)
            {
                throw new AudioRenderSoundFontException(
                    AudioRenderSoundFontFailure.ExternalHashChangeRequiresConfirmation,
                    "The external SoundFont content differs from the stored Project identity and requires explicit acceptance.",
                    verification.CurrentSha256,
                    verification.CurrentFileSizeBytes);
            }
            if (verification.HashMatches == false)
            {
                diagnostics.Add(new(
                    "MIDORA-AUDIO-RENDER-SF2-HASH-CHANGED",
                    AudioRenderDiagnosticSeverity.Warning,
                    "The explicitly accepted external SoundFont content differs from the stored Project identity; the Project record was not modified."));
            }
        }
        else
        {
            EmbeddedProjectSoundFontReference embedded = (EmbeddedProjectSoundFontReference)reference;
            if (embeddedResource is null
                || !embeddedResource.IsAvailable
                || embeddedResource.Reference != embedded
                || string.IsNullOrWhiteSpace(embeddedResource.ResolvedAbsolutePath)
                || !File.Exists(embeddedResource.ResolvedAbsolutePath))
            {
                throw new AudioRenderSoundFontException(
                    AudioRenderSoundFontFailure.EmbeddedResourceUnavailable,
                    "The embedded Project SoundFont runtime resource is unavailable or does not match the Project reference.");
            }
            sourcePath = embeddedResource.ResolvedAbsolutePath;
        }

        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-audio-render-sf2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string frozenPath = Path.Combine(directory, "project-soundfont.sf2");
        try
        {
            SoundFontContentIdentityV1 frozen = await CopyAndHashAsync(
                sourcePath,
                frozenPath,
                cancellationToken).ConfigureAwait(false);
            if (verifiedSha256 is not null
                && (!string.Equals(verifiedSha256, frozen.Sha256, StringComparison.Ordinal)
                    || verifiedSize != frozen.FileSizeBytes))
            {
                throw new AudioRenderSoundFontException(
                    AudioRenderSoundFontFailure.ContentChangedDuringFreeze,
                    "The external SoundFont changed while the render snapshot was being frozen.",
                    frozen.Sha256,
                    frozen.FileSizeBytes);
            }
            if (reference is EmbeddedProjectSoundFontReference
                && (!string.Equals(reference.Sha256, frozen.Sha256, StringComparison.Ordinal)
                    || reference.FileSizeBytes != frozen.FileSizeBytes))
            {
                throw new AudioRenderSoundFontException(
                    AudioRenderSoundFontFailure.EmbeddedResourceUnavailable,
                    "The embedded SoundFont snapshot does not match the stored Project identity.",
                    frozen.Sha256,
                    frozen.FileSizeBytes);
            }
            return new(
                reference,
                sourcePath,
                frozenPath,
                directory,
                frozen.Sha256,
                frozen.FileSizeBytes,
                diagnostics.ToArray());
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        string? directory = Interlocked.Exchange(ref _ownedDirectory, null);
        if (directory is not null)
        {
            TryDeleteDirectory(directory);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<SoundFontContentIdentityV1> CopyAndHashAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream target = new(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length = checked(length + read);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new(
                Path.GetFileName(sourcePath),
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                length);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or CryptographicException)
        {
            throw new AudioRenderSoundFontException(
                AudioRenderSoundFontFailure.Unreadable,
                "The Project SoundFont could not be frozen for audio rendering.",
                innerException: exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            string fullPath = Path.GetFullPath(directory);
            string expectedPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("midora-audio-render-sf2-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller owns task-level cleanup reporting; snapshot disposal remains best-effort.
        }
    }
}
