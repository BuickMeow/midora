using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public enum AudioCacheRetentionState
{
    Enabled,
    DisabledByPreference,
    DisabledByQuota,
    DisabledByWriteFailure
}

public enum AudioCacheWarningCode
{
    None,
    AudioCacheRetentionDisabled,
    AudioCacheCorruptAndRebuilt
}

public readonly record struct AudioCacheWarning(
    AudioCacheWarningCode Code,
    string Message);

public readonly record struct AudioCacheSessionSnapshot(
    string RootPath,
    string SessionPath,
    long ReusableBytes,
    long MaximumReusableBytes,
    long TransientBytes,
    long PeakTransientBytes,
    AudioCacheRetentionState RetentionState,
    AudioCacheWarning Warning);

public readonly record struct AudioCachePublishResult(
    bool Published,
    bool AlreadyPresent,
    AudioCacheRetentionState RetentionState,
    AudioCacheWarning Warning);

public interface IAudioPcmCacheSessionAccess
{
    AudioCacheSessionSnapshot? AudioCacheSnapshot { get; }

    bool TryCopyReusableAudio(string key, Stream destination, out long payloadLength);

    AudioCachePublishResult PublishReusableAudio(
        string key,
        Stream source,
        long payloadLength);

    void InvalidateReusableAudio(string key);

    AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(long lengthBytes);

    void DisableReusableAudioRetention(string reason);
}

public sealed class AudioRecoveryStorageUnavailableException : IOException
{
    public AudioRecoveryStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string Code => "AudioRecoveryStorageUnavailable";
}

public sealed class AudioCacheSessionStore : IDisposable
{
    private const string SessionPrefix = "session-";
    private const string ManifestFileName = "session.manifest";
    private const string ActiveLockFileName = "session.active.lock";
    private const string ManifestMagic = "MIDORA_AUDIO_CACHE_SESSION_V1";
    private const uint EntryMagic = 0x4341434D; // MCAC, little-endian.
    private const uint EntryVersion = 1;
    private const int EntryHeaderSize = 4 + 4 + 8 + 32;
    private readonly object _sync = new();
    private readonly string _rootPath;
    private readonly string _sessionPath;
    private readonly string _reusablePath;
    private readonly string _transientPath;
    private readonly long _maximumReusableBytes;
    private readonly FileStream _activeLock;
    private long _reusableBytes;
    private long _transientBytes;
    private long _peakTransientBytes;
    private AudioCacheRetentionState _retentionState;
    private AudioCacheWarning _warning;
    private bool _disposed;

    public AudioCacheSessionStore(string rootPath, long maximumReusableBytes)
    {
        _rootPath = NormalizeLocalRoot(rootPath);
        if (maximumReusableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumReusableBytes));
        }
        _maximumReusableBytes = maximumReusableBytes;
        _retentionState = maximumReusableBytes == 0
            ? AudioCacheRetentionState.DisabledByPreference
            : AudioCacheRetentionState.Enabled;
        if (_retentionState == AudioCacheRetentionState.DisabledByPreference)
        {
            _warning = RetentionWarning("Reusable audio cache retention is disabled by its zero-byte quota.");
        }

        Directory.CreateDirectory(_rootPath);
        string sessionName = SessionPrefix + Guid.NewGuid().ToString("N");
        _sessionPath = ValidateOwnedSessionPath(_rootPath, Path.Combine(_rootPath, sessionName));
        _reusablePath = Path.Combine(_sessionPath, "reusable");
        _transientPath = Path.Combine(_sessionPath, "transient");
        try
        {
            Directory.CreateDirectory(_sessionPath);
            Directory.CreateDirectory(_reusablePath);
            Directory.CreateDirectory(_transientPath);
            File.WriteAllText(
                Path.Combine(_sessionPath, ManifestFileName),
                ManifestMagic + "\n" + sessionName + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _activeLock = new FileStream(
                Path.Combine(_sessionPath, ActiveLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch
        {
            TryDeleteOwnedSession(_rootPath, _sessionPath);
            throw;
        }
    }

    public string RootPath => _rootPath;
    public string SessionPath => _sessionPath;

    public static string ComputeKey(ReadOnlySpan<byte> canonicalKeyBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(canonicalKeyBytes));

    public AudioCacheSessionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(
                _rootPath,
                _sessionPath,
                _reusableBytes,
                _maximumReusableBytes,
                _transientBytes,
                _peakTransientBytes,
                _retentionState,
                _warning);
        }
    }

    public AudioCachePublishResult PublishReusable(string key, ReadOnlySpan<byte> payload)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string finalPath = GetReusableEntryPath(key);
            if (TryReadEntry(finalPath, out _))
            {
                return new(true, true, _retentionState, _warning);
            }
            if (_retentionState != AudioCacheRetentionState.Enabled)
            {
                return new(false, false, _retentionState, _warning);
            }

            long entryBytes = checked(EntryHeaderSize + (long)payload.Length);
            if (entryBytes > _maximumReusableBytes - _reusableBytes)
            {
                DisableRetention(
                    AudioCacheRetentionState.DisabledByQuota,
                    "Reusable audio cache quota is full; new entries will be rendered without retention.");
                return new(false, false, _retentionState, _warning);
            }

            string temporaryPath = Path.Combine(
                _reusablePath,
                $".{key}.{Guid.NewGuid():N}.tmp");
            try
            {
                byte[] digest = SHA256.HashData(payload);
                Span<byte> header = stackalloc byte[EntryHeaderSize];
                BinaryPrimitives.WriteUInt32LittleEndian(header, EntryMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], EntryVersion);
                BinaryPrimitives.WriteInt64LittleEndian(header[8..], payload.Length);
                digest.CopyTo(header[16..]);
                using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan))
                {
                    stream.Write(header);
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, finalPath, overwrite: false);
                _reusableBytes = checked(_reusableBytes + entryBytes);
                return new(true, false, _retentionState, _warning);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                TryDeleteFile(temporaryPath);
                DisableRetention(
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    "Reusable audio cache writes failed; new cache misses will be rendered without retention. "
                        + exception.Message);
                return new(false, false, _retentionState, _warning);
            }
        }
    }

    public bool TryReadReusable(string key, out byte[] payload)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string path = GetReusableEntryPath(key);
            if (TryReadEntry(path, out payload))
            {
                return true;
            }
            payload = [];
            return false;
        }
    }

    public bool TryCopyReusable(string key, Stream destination, out long payloadLength)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The cache destination stream must be writable.", nameof(destination));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return TryCopyEntry(GetReusableEntryPath(key), destination, out payloadLength);
        }
    }

    public AudioCachePublishResult PublishReusable(
        string key,
        Stream source,
        long payloadLength)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The cache source stream must be readable.", nameof(source));
        }
        if (payloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string finalPath = GetReusableEntryPath(key);
            if (TryCopyEntry(finalPath, Stream.Null, out _))
            {
                return new(true, true, _retentionState, _warning);
            }
            if (_retentionState != AudioCacheRetentionState.Enabled)
            {
                return new(false, false, _retentionState, _warning);
            }

            long entryBytes = checked(EntryHeaderSize + payloadLength);
            if (entryBytes > _maximumReusableBytes - _reusableBytes)
            {
                DisableRetention(
                    AudioCacheRetentionState.DisabledByQuota,
                    "Reusable audio cache quota is full; new entries will be rendered without retention.");
                return new(false, false, _retentionState, _warning);
            }

            string temporaryPath = Path.Combine(
                _reusablePath,
                $".{key}.{Guid.NewGuid():N}.tmp");
            try
            {
                using FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                stream.Position = EntryHeaderSize;
                byte[] buffer = new byte[64 * 1024];
                using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long remaining = payloadLength;
                while (remaining != 0)
                {
                    int requested = (int)Math.Min(buffer.Length, remaining);
                    int read = source.Read(buffer, 0, requested);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The reusable audio cache source is truncated.");
                    }
                    digest.AppendData(buffer, 0, read);
                    stream.Write(buffer, 0, read);
                    remaining -= read;
                }

                byte[] hash = digest.GetHashAndReset();
                Span<byte> header = stackalloc byte[EntryHeaderSize];
                BinaryPrimitives.WriteUInt32LittleEndian(header, EntryMagic);
                BinaryPrimitives.WriteUInt32LittleEndian(header[4..], EntryVersion);
                BinaryPrimitives.WriteInt64LittleEndian(header[8..], payloadLength);
                hash.CopyTo(header[16..]);
                stream.Position = 0;
                stream.Write(header);
                stream.Flush(flushToDisk: true);
                stream.Dispose();
                File.Move(temporaryPath, finalPath, overwrite: false);
                _reusableBytes = checked(_reusableBytes + entryBytes);
                return new(true, false, _retentionState, _warning);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                TryDeleteFile(temporaryPath);
                DisableRetention(
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    "Reusable audio cache writes failed; new cache misses will be rendered without retention. "
                        + exception.Message);
                return new(false, false, _retentionState, _warning);
            }
        }
    }

    public void InvalidateReusable(string key)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string path = GetReusableEntryPath(key);
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            TryDeleteFile(path);
            _reusableBytes = Math.Max(0, _reusableBytes - length);
            _warning = new(
                AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
                "An invalid reusable audio PCM payload was isolated and will be rebuilt.");
        }
    }

    public AudioRecoverySpool CreateRecoverySpool(long lengthBytes)
    {
        if (lengthBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthBytes));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string path = Path.Combine(
                _transientPath,
                $"recovery-{Guid.NewGuid():N}.spool");
            try
            {
                FileStream stream = new(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan);
                try
                {
                    stream.SetLength(lengthBytes);
                    stream.Position = 0;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
                _transientBytes = checked(_transientBytes + lengthBytes);
                _peakTransientBytes = Math.Max(_peakTransientBytes, _transientBytes);
                return new AudioRecoverySpool(this, path, stream, lengthBytes);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                TryDeleteFile(path);
                throw new AudioRecoveryStorageUnavailableException(
                    "The complete Buffering recovery interval could not be reserved in the transient audio spool.",
                    exception);
            }
        }
    }

    public void DisableReusableRetention(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retentionState == AudioCacheRetentionState.Enabled)
            {
                DisableRetention(AudioCacheRetentionState.DisabledByWriteFailure, reason);
            }
        }
    }

    public int ClearInactiveSessions()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int removed = 0;
            foreach (string candidate in Directory.EnumerateDirectories(
                _rootPath,
                SessionPrefix + "*",
                SearchOption.TopDirectoryOnly))
            {
                string path = ValidateOwnedSessionPath(_rootPath, candidate);
                if (string.Equals(path, _sessionPath, StringComparison.OrdinalIgnoreCase)
                    || !HasValidManifest(path)
                    || IsSessionActive(path))
                {
                    continue;
                }
                Directory.Delete(path, recursive: true);
                removed++;
            }
            return removed;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _activeLock.Dispose();
            TryDeleteOwnedSession(_rootPath, _sessionPath);
        }
        GC.SuppressFinalize(this);
    }

    private bool TryReadEntry(string path, out byte[] payload)
    {
        payload = [];
        if (!File.Exists(path))
        {
            return false;
        }
        long fileLength = 0;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            fileLength = stream.Length;
            Span<byte> header = stackalloc byte[EntryHeaderSize];
            stream.ReadExactly(header);
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            if (magic != EntryMagic
                || version != EntryVersion
                || payloadLength < 0
                || payloadLength > int.MaxValue
                || fileLength != EntryHeaderSize + payloadLength)
            {
                throw new InvalidDataException("The reusable audio cache entry header is invalid.");
            }
            payload = new byte[(int)payloadLength];
            stream.ReadExactly(payload);
            Span<byte> actualDigest = stackalloc byte[32];
            SHA256.HashData(payload, actualDigest);
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, header[16..]))
            {
                throw new InvalidDataException("The reusable audio cache entry checksum is invalid.");
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException)
        {
            TryDeleteFile(path);
            if (fileLength > 0)
            {
                _reusableBytes = Math.Max(0, _reusableBytes - fileLength);
            }
            _warning = new(
                AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
                "A corrupt reusable audio cache entry was isolated and will be rebuilt. "
                    + exception.Message);
            payload = [];
            return false;
        }
    }

    private bool TryCopyEntry(string path, Stream destination, out long payloadLength)
    {
        payloadLength = 0;
        if (!File.Exists(path))
        {
            return false;
        }
        long fileLength = 0;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            fileLength = stream.Length;
            Span<byte> header = stackalloc byte[EntryHeaderSize];
            stream.ReadExactly(header);
            long length = ValidateEntryHeader(header, fileLength);
            byte[] buffer = new byte[64 * 1024];
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long remaining = length;
            while (remaining != 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read == 0)
                {
                    throw new EndOfStreamException("The reusable audio cache entry is truncated.");
                }
                digest.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
            byte[] actualDigest = digest.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, header[16..]))
            {
                throw new InvalidDataException("The reusable audio cache entry checksum is invalid.");
            }
            payloadLength = length;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException)
        {
            TryDeleteFile(path);
            if (fileLength > 0)
            {
                _reusableBytes = Math.Max(0, _reusableBytes - fileLength);
            }
            _warning = new(
                AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
                "A corrupt reusable audio cache entry was isolated and will be rebuilt. "
                    + exception.Message);
            payloadLength = 0;
            return false;
        }
    }

    private static long ValidateEntryHeader(ReadOnlySpan<byte> header, long fileLength)
    {
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        if (magic != EntryMagic
            || version != EntryVersion
            || payloadLength < 0
            || fileLength != EntryHeaderSize + payloadLength)
        {
            throw new InvalidDataException("The reusable audio cache entry header is invalid.");
        }
        return payloadLength;
    }

    private void ReleaseRecoverySpool(string path, FileStream? stream, long lengthBytes)
    {
        lock (_sync)
        {
            try
            {
                stream?.Dispose();
            }
            finally
            {
                TryDeleteFile(path);
                _transientBytes = Math.Max(0, _transientBytes - lengthBytes);
            }
        }
    }

    private string GetReusableEntryPath(string key) => Path.Combine(_reusablePath, key + ".mcac");

    private void DisableRetention(AudioCacheRetentionState state, string message)
    {
        _retentionState = state;
        _warning = RetentionWarning(message);
    }

    private static AudioCacheWarning RetentionWarning(string message) => new(
        AudioCacheWarningCode.AudioCacheRetentionDisabled,
        message);

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length != 64 || key.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "An audio cache key must be a lowercase SHA-256 hexadecimal string.",
                nameof(key));
        }
    }

    private static string NormalizeLocalRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath)
            || rootPath.StartsWith("\\\\", StringComparison.Ordinal)
            || rootPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The audio cache root must be a fully-qualified local path; UNC and network paths are not supported.",
                nameof(rootPath));
        }
        string result = Path.GetFullPath(rootPath);
        string? root = Path.GetPathRoot(result);
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException("The audio cache root has no local volume root.", nameof(rootPath));
        }
        try
        {
            if (new DriveInfo(root).DriveType == DriveType.Network)
            {
                throw new ArgumentException(
                    "The audio cache root must not use a mapped network drive.",
                    nameof(rootPath));
            }
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new ArgumentException(
                "The audio cache root volume could not be verified as local.",
                nameof(rootPath),
                exception);
        }
        return string.Equals(result, root, StringComparison.OrdinalIgnoreCase)
            ? result
            : Path.TrimEndingDirectorySeparator(result);
    }

    private static string ValidateOwnedSessionPath(string rootPath, string candidate)
    {
        string fullPath = Path.GetFullPath(candidate);
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(parent, rootPath, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(SessionPrefix, StringComparison.Ordinal)
            || name.Length <= SessionPrefix.Length)
        {
            throw new InvalidDataException("The audio cache session path is outside the configured cache root.");
        }
        return fullPath;
    }

    private static bool HasValidManifest(string sessionPath)
    {
        try
        {
            string content = File.ReadAllText(
                Path.Combine(sessionPath, ManifestFileName),
                Encoding.UTF8);
            string name = Path.GetFileName(sessionPath);
            return string.Equals(
                content,
                ManifestMagic + "\n" + name + "\n",
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsSessionActive(string sessionPath)
    {
        string lockPath = Path.Combine(sessionPath, ActiveLockFileName);
        try
        {
            using FileStream ignored = new(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteOwnedSession(string rootPath, string sessionPath)
    {
        try
        {
            string validated = ValidateOwnedSessionPath(rootPath, sessionPath);
            if (Directory.Exists(validated) && HasValidManifest(validated))
            {
                Directory.Delete(validated, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            // Session cleanup is best effort. The next explicit Clear Inactive Cache can retry.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller already reports the primary cache failure.
        }
    }

    public sealed class AudioRecoverySpool : IDisposable
    {
        private AudioCacheSessionStore? _owner;
        private FileStream? _stream;

        internal AudioRecoverySpool(
            AudioCacheSessionStore owner,
            string path,
            FileStream stream,
            long lengthBytes)
        {
            _owner = owner;
            _stream = stream;
            Path = path;
            LengthBytes = lengthBytes;
        }

        public string Path { get; }
        public long LengthBytes { get; }
        public Stream Stream => _stream ?? throw new ObjectDisposedException(nameof(AudioRecoverySpool));

        public void ReleaseFileHandleForExternalUse()
        {
            if (Volatile.Read(ref _owner) is null)
            {
                throw new ObjectDisposedException(nameof(AudioRecoverySpool));
            }

            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            stream?.Dispose();
        }

        public void Dispose()
        {
            AudioCacheSessionStore? owner = Interlocked.Exchange(ref _owner, null);
            FileStream? stream = Interlocked.Exchange(ref _stream, null);
            if (owner is not null)
            {
                owner.ReleaseRecoverySpool(Path, stream, LengthBytes);
            }
            else
            {
                stream?.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
