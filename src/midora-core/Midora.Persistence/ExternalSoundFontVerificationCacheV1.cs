using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Midora.Domain;

namespace Midora.Persistence;

public sealed record ExternalSoundFontResolutionV1(
    ExternalSoundFontResolutionKind Resolution,
    string? ResolvedAbsolutePath)
{
    public bool IsReadable => Resolution is
        ExternalSoundFontResolutionKind.Exact or
        ExternalSoundFontResolutionKind.CaseInsensitiveFallback;
}

public sealed record ExternalSoundFontFileStampV1(
    string ResolvedAbsolutePath,
    uint VolumeSerialNumber,
    ulong FileId,
    long FileSizeBytes,
    long LastWriteFileTimeUtc);

public sealed class ExternalSoundFontVerificationCacheV1 : IDisposable
{
    private readonly object _sync = new();
    private CacheEntry? _cached;
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private long _invalidationVersion;
    private int _fullHashComputationCount;
    private bool _invalidated = true;
    private bool _disposed;

    public bool IsInvalidated
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _invalidated;
            }
        }
    }

    public event EventHandler? Invalidated;

    public Task<ExternalSoundFontVerificationV1> VerifyAsync(
        string projectFilePath,
        ExternalProjectSoundFontReference reference,
        bool forceFullVerification = false,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(
            projectFilePath,
            reference,
            forceFullVerification,
            retryCount: 0,
            cancellationToken);

    private async Task<ExternalSoundFontVerificationV1> VerifyCoreAsync(
        string projectFilePath,
        ExternalProjectSoundFontReference reference,
        bool forceFullVerification,
        int retryCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ExternalSoundFontResolutionV1 resolution = SoundFontBindingV1.ResolveExternal(
            projectFilePath,
            reference);
        if (!resolution.IsReadable || resolution.ResolvedAbsolutePath is null)
        {
            ClearUnavailableCache();
            return new(resolution.Resolution, null, null, null, null);
        }

        string resolvedPath = Path.GetFullPath(resolution.ResolvedAbsolutePath);
        ConfigureWatcher(resolvedPath);
        ExternalSoundFontFileStampV1 currentStamp;
        try
        {
            currentStamp = SoundFontFileSnapshotReaderV1.CaptureStamp(resolvedPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            ClearUnavailableCache();
            return new(ExternalSoundFontResolutionKind.Unreadable, null, null, null, null);
        }

        long validationVersion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!forceFullVerification
                && !_invalidated
                && _cached is not null
                && _cached.Matches(reference, currentStamp))
            {
                return _cached.Result;
            }
            validationVersion = _invalidationVersion;
        }

        ExternalSoundFontVerificationV1 verified = await SoundFontBindingV1.VerifyExternalAsync(
            projectFilePath,
            reference,
            cancellationToken).ConfigureAwait(false);
        bool stable;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fullHashComputationCount++;
            stable = verified.FileStamp is null
                || validationVersion == _invalidationVersion
                    && StampsMatch(currentStamp, verified.FileStamp);
            if (verified.FileStamp is not null && stable)
            {
                _cached = new(reference, verified.FileStamp, verified);
                _invalidated = false;
            }
            else if (verified.FileStamp is null)
            {
                _cached = null;
                _invalidated = true;
            }
        }
        if (!stable)
        {
            if (retryCount >= 2)
            {
                throw new IOException(
                    "The external SoundFont kept changing during complete verification.");
            }
            return await VerifyCoreAsync(
                projectFilePath,
                reference,
                forceFullVerification: true,
                retryCount + 1,
                cancellationToken).ConfigureAwait(false);
        }
        return verified;
    }

    public void Invalidate() => InvalidateCore(ignoreDisposed: false);

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _cached = null;
        }
        watcher?.Dispose();
    }

    internal int FullHashComputationCount
    {
        get
        {
            lock (_sync)
            {
                return _fullHashComputationCount;
            }
        }
    }

    private void ConfigureWatcher(string resolvedPath)
    {
        string normalizedPath = Path.GetFullPath(resolvedPath);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.Equals(_watchedPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _watcher?.Dispose();
            string directory = Path.GetDirectoryName(normalizedPath)
                ?? throw new InvalidDataException("The external SoundFont has no parent directory.");
            FileSystemWatcher watcher = new(directory, Path.GetFileName(normalizedPath))
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.Attributes
                    | NotifyFilters.Size
                    | NotifyFilters.LastWrite
                    | NotifyFilters.CreationTime
                    | NotifyFilters.Security
            };
            watcher.Changed += OnWatcherChanged;
            watcher.Created += OnWatcherChanged;
            watcher.Deleted += OnWatcherChanged;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watchedPath = normalizedPath;
        }
    }

    private void ClearUnavailableCache()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cached = null;
            _invalidated = true;
        }
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e) =>
        InvalidateCore(ignoreDisposed: true);

    private void OnWatcherRenamed(object sender, RenamedEventArgs e) =>
        InvalidateCore(ignoreDisposed: true);

    private void OnWatcherError(object sender, ErrorEventArgs e) =>
        InvalidateCore(ignoreDisposed: true);

    private void InvalidateCore(bool ignoreDisposed)
    {
        EventHandler? handler;
        lock (_sync)
        {
            if (_disposed)
            {
                if (ignoreDisposed)
                {
                    return;
                }
                throw new ObjectDisposedException(nameof(ExternalSoundFontVerificationCacheV1));
            }
            _invalidated = true;
            _invalidationVersion++;
            handler = Invalidated;
        }
        handler?.Invoke(this, EventArgs.Empty);
    }

    private sealed record CacheEntry(
        ExternalProjectSoundFontReference Reference,
        ExternalSoundFontFileStampV1 Stamp,
        ExternalSoundFontVerificationV1 Result)
    {
        public bool Matches(
            ExternalProjectSoundFontReference reference,
            ExternalSoundFontFileStampV1 stamp) =>
            Reference == reference
            && StampsMatch(Stamp, stamp);
    }

    private static bool StampsMatch(
        ExternalSoundFontFileStampV1 first,
        ExternalSoundFontFileStampV1 second) =>
        first.VolumeSerialNumber == second.VolumeSerialNumber
        && first.FileId == second.FileId
        && first.FileSizeBytes == second.FileSizeBytes
        && first.LastWriteFileTimeUtc == second.LastWriteFileTimeUtc
        && string.Equals(
            first.ResolvedAbsolutePath,
            second.ResolvedAbsolutePath,
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record SoundFontFileSnapshotV1(
    SoundFontContentIdentityV1 Identity,
    ExternalSoundFontFileStampV1 Stamp);

internal static partial class SoundFontFileSnapshotReaderV1
{
    public static ExternalSoundFontFileStampV1 CaptureStamp(string soundFontPath)
    {
        string path = NormalizePath(soundFontPath);
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.SequentialScan);
        return ReadStamp(handle, path);
    }

    public static async Task<SoundFontFileSnapshotV1> ReadAsync(
        string soundFontPath,
        CancellationToken cancellationToken)
    {
        string path = NormalizePath(soundFontPath);
        string originalFileName = Path.GetFileName(path);
        SoundFontReferenceValidation.ValidateOriginalFileName(originalFileName);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ExternalSoundFontFileStampV1 before = ReadStamp(stream.SafeFileHandle, path);
        byte[] hash = await System.Security.Cryptography.SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        ExternalSoundFontFileStampV1 after = ReadStamp(stream.SafeFileHandle, path);
        if (before != after)
        {
            throw new IOException("The SoundFont changed while its complete content identity was being read.");
        }
        return new(
            new(originalFileName, Convert.ToHexStringLower(hash), before.FileSizeBytes),
            before);
    }

    private static ExternalSoundFontFileStampV1 ReadStamp(
        SafeFileHandle handle,
        string resolvedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Midora initial-release SoundFont file identity requires Windows.");
        }
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        long fileSize = checked((long)(((ulong)information.FileSizeHigh << 32)
            | information.FileSizeLow));
        long lastWrite = unchecked((long)(((ulong)information.LastWriteTimeHigh << 32)
            | information.LastWriteTimeLow));
        return new(
            Path.GetFullPath(resolvedPath),
            information.VolumeSerialNumber,
            fileId,
            fileSize,
            lastWrite);
    }

    private static string NormalizePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(value));
        }
        return Path.GetFullPath(value);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
