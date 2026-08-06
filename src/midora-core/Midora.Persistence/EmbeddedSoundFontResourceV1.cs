using System.Buffers;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

public enum EmbeddedSoundFontResourceStatusV1
{
    Available,
    RuntimeResourceMissing,
    ReferenceMismatch,
    MissingManifestEntry,
    InvalidManifestEntry,
    MissingPackageEntry,
    SizeMismatch,
    HashMismatch,
    Unreadable
}

public sealed class EmbeddedSoundFontResourceV1 : IDisposable, IAsyncDisposable
{
    private string? _ownedDirectory;

    internal EmbeddedSoundFontResourceV1(
        EmbeddedProjectSoundFontReference reference,
        EmbeddedSoundFontResourceStatusV1 status,
        string? resolvedAbsolutePath,
        string? ownedDirectory,
        string? actualSha256,
        long? actualFileSizeBytes)
    {
        Reference = reference;
        Status = status;
        ResolvedAbsolutePath = resolvedAbsolutePath;
        _ownedDirectory = ownedDirectory;
        ActualSha256 = actualSha256;
        ActualFileSizeBytes = actualFileSizeBytes;
    }

    public EmbeddedProjectSoundFontReference Reference { get; }
    public EmbeddedSoundFontResourceStatusV1 Status { get; }
    public string? ResolvedAbsolutePath { get; }
    public string? ActualSha256 { get; }
    public long? ActualFileSizeBytes { get; }
    public bool IsAvailable => Status == EmbeddedSoundFontResourceStatusV1.Available;

    public void Dispose()
    {
        string? directory = Interlocked.Exchange(ref _ownedDirectory, null);
        if (directory is null)
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Runtime extraction cleanup is best-effort. Package save cleanup has its own diagnostics.
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal string RequireReadablePath(EmbeddedProjectSoundFontReference expectedReference)
    {
        ArgumentNullException.ThrowIfNull(expectedReference);
        if (!IsAvailable
            || ResolvedAbsolutePath is null)
        {
            throw new EmbeddedSoundFontResourceUnavailableExceptionV1(Status);
        }
        if (Reference != expectedReference)
        {
            throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                EmbeddedSoundFontResourceStatusV1.ReferenceMismatch);
        }
        if (!File.Exists(ResolvedAbsolutePath))
        {
            throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                EmbeddedSoundFontResourceStatusV1.RuntimeResourceMissing);
        }
        return ResolvedAbsolutePath;
    }

    internal FileStream OpenReadForSave(EmbeddedProjectSoundFontReference expectedReference)
    {
        string path = RequireReadablePath(expectedReference);
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EmbeddedSoundFontResourceUnavailableExceptionV1(
                EmbeddedSoundFontResourceStatusV1.Unreadable,
                innerException: exception);
        }
    }

    internal static async Task<EmbeddedSoundFontResourceV1> ImportAndBindAsync(
        MidoraProject project,
        string selectedSoundFontPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        string sourcePath = NormalizeAbsolutePath(selectedSoundFontPath, nameof(selectedSoundFontPath));
        string originalFileName = Path.GetFileName(sourcePath);
        SoundFontReferenceValidation.ValidateOriginalFileName(originalFileName);

        string directory = CreateRuntimeDirectory();
        string snapshotPath = Path.Combine(directory, "soundfont.sf2");
        try
        {
            await using FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream destination = new(
                snapshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            StreamCopyIdentityV1 identity = await CopyAndHashAsync(
                source, destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            MidoraId resourceId = project.SoundFont.SetEmbedded(
                project,
                originalFileName,
                identity.Sha256,
                identity.FileSizeBytes);
            EmbeddedProjectSoundFontReference reference =
                (EmbeddedProjectSoundFontReference)project.SoundFont.Reference!;
            if (reference.ResourceId != resourceId)
            {
                throw new InvalidOperationException("Embedded SoundFont binding did not preserve its allocated ID.");
            }
            return new(
                reference,
                EmbeddedSoundFontResourceStatusV1.Available,
                snapshotPath,
                directory,
                identity.Sha256,
                identity.FileSizeBytes);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    internal static async Task<StreamCopyIdentityV1> CopyAndHashAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(buffer, 0, count);
                length = checked(length + count);
            }
            return new(Convert.ToHexStringLower(hash.GetHashAndReset()), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static string CreateRuntimeDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-embedded-sf2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void TryDeleteDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string NormalizeAbsolutePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Path must be fully qualified.", parameterName);
        }
        return Path.GetFullPath(value);
    }
}

internal sealed record StreamCopyIdentityV1(string Sha256, long FileSizeBytes);

internal sealed class EmbeddedSoundFontResourceUnavailableExceptionV1(
    EmbeddedSoundFontResourceStatusV1 status,
    string? actualSha256 = null,
    long? actualFileSizeBytes = null,
    Exception? innerException = null)
    : Exception(
        "The Embedded SoundFont runtime resource is unavailable or no longer matches the Project.",
        innerException)
{
    public EmbeddedSoundFontResourceStatusV1 Status { get; } = status;
    public string? ActualSha256 { get; } = actualSha256;
    public long? ActualFileSizeBytes { get; } = actualFileSizeBytes;
}
