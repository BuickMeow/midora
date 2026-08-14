using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;

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
    AudioCacheCorruptAndRebuilt,
    AudioCachePerformanceBelowRequirement
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
    AudioCacheWarning Warning,
    long PhysicalReusableBytes = 0,
    long DeadReusableBytes = 0,
    int PackFileCount = 0,
    long SequentialReadBytesPerSecond = 0,
    long SequentialWriteBytesPerSecond = 0,
    bool MeetsStoragePerformanceRequirement = false,
    long WriterBacklogBytes = 0,
    int PendingPublishCount = 0,
    bool CompactionPending = false);

public readonly record struct AudioCachePublishResult(
    bool Published,
    bool AlreadyPresent,
    AudioCacheRetentionState RetentionState,
    AudioCacheWarning Warning);

public readonly record struct AudioCachePublishSlice(
    string Key,
    long PayloadOffset,
    long PayloadLength);

public readonly record struct AudioCacheGenerationBinding(
    string Owner,
    string Key);

public interface IAudioPcmCacheSessionAccess
{
    AudioCacheSessionSnapshot? AudioCacheSnapshot { get; }

    bool TryCopyReusableAudio(string key, Stream destination, out long payloadLength);

    ReusableAudioReadLease? AcquireReusableAudioReadLease(
        IReadOnlyList<string> keys) => null;

    AudioCachePublishResult PublishReusableAudio(
        string key,
        Stream source,
        long payloadLength);

    void QueueReusableAudioBatch(
        AudioCacheSessionStore.AudioRecoverySpool spool,
        IReadOnlyList<AudioCachePublishSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(slices);
        spool.ReleaseFileHandleForExternalUse();
        try
        {
            using FileStream source = new(
                spool.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            foreach (AudioCachePublishSlice slice in slices)
            {
                source.Position = slice.PayloadOffset;
                _ = PublishReusableAudio(slice.Key, source, slice.PayloadLength);
            }
        }
        finally
        {
            spool.Dispose();
        }
    }

    void InvalidateReusableAudio(string key);

    void RegisterReusableAudioGeneration(string owner, string key)
    {
    }

    void RegisterReusableAudioGenerations(
        IReadOnlyList<AudioCacheGenerationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (AudioCacheGenerationBinding binding in bindings)
        {
            RegisterReusableAudioGeneration(binding.Owner, binding.Key);
        }
    }

    bool TryCompactReusableAudio(bool isStopped, TimeSpan idleDuration) => false;

    AudioCacheSessionStore.AudioRecoverySpool CreateTransientAudioSpool(long lengthBytes);

    AudioCacheSessionStore.AudioRecoverySpool CreateSparseTransientAudioSpool(long lengthBytes) =>
        CreateTransientAudioSpool(lengthBytes);

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
    private readonly AudioCachePackStore _packStore;
    private readonly AudioCacheStorageBenchmarkResult _storageBenchmark;
    private readonly Queue<PendingPublishBatch> _publishQueue = new();
    private readonly Dictionary<string, PendingPublishSlice> _pendingPublishes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingReadLeaseCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioRecoverySpool> _deferredPendingSpools =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Thread _publishThread;
    private long _reusableBytes;
    private long _transientBytes;
    private long _peakTransientBytes;
    private long _pendingPublishBytes;
    private AudioCacheRetentionState _retentionState;
    private AudioCacheWarning _warning;
    private bool _disposed;
    private bool _publishStopRequested;
    private bool _compactionRequested;
    private int _activeReusableReadLeaseCount;
    private bool _compactionStopped;
    private TimeSpan _compactionIdleDuration;

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
        FileStream? createdActiveLock = null;
        AudioCachePackStore? createdPackStore = null;
        try
        {
            Directory.CreateDirectory(_sessionPath);
            Directory.CreateDirectory(_reusablePath);
            Directory.CreateDirectory(_transientPath);
            File.WriteAllText(
                Path.Combine(_sessionPath, ManifestFileName),
                ManifestMagic + "\n" + sessionName + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            createdActiveLock = new FileStream(
                Path.Combine(_sessionPath, ActiveLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            _activeLock = createdActiveLock;
            createdPackStore = new AudioCachePackStore(
                _reusablePath,
                maximumReusableBytes);
            _packStore = createdPackStore;
            _storageBenchmark = maximumReusableBytes >= 1024L * 1024 * 1024
                ? AudioCacheStorageBenchmark.Measure(_transientPath)
                : AudioCacheStorageBenchmarkResult.NotMeasured;
            if (maximumReusableBytes != 0
                && _storageBenchmark != default
                && !_storageBenchmark.MeetsMinimumRequirement)
            {
                _warning = new(
                    AudioCacheWarningCode.AudioCachePerformanceBelowRequirement,
                    "The configured audio cache volume is below the supported sequential throughput: "
                        + $"read {_storageBenchmark.SequentialReadBytesPerSecond / (1024 * 1024)} MiB/s, "
                        + $"write {_storageBenchmark.SequentialWriteBytesPerSecond / (1024 * 1024)} MiB/s; "
                        + "Midora requires a local SSD with at least 200 MiB/s read and 100 MiB/s write.");
            }
            _publishThread = new Thread(RunPublishQueue)
            {
                IsBackground = true,
                Name = "Midora Audio Pack Writer",
                Priority = ThreadPriority.BelowNormal
            };
            _publishThread.Start();
        }
        catch
        {
            createdPackStore?.Dispose();
            createdActiveLock?.Dispose();
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
                _warning,
                _packStore.PhysicalBytes,
                _packStore.DeadBytes,
                _packStore.PackCount,
                _storageBenchmark.SequentialReadBytesPerSecond,
                _storageBenchmark.SequentialWriteBytesPerSecond,
                _storageBenchmark.MeetsMinimumRequirement,
                _pendingPublishBytes,
                _pendingPublishes.Count,
                _compactionRequested);
        }
    }

    public AudioCachePublishResult PublishReusable(string key, ReadOnlySpan<byte> payload)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_packStore.Contains(key))
            {
                return new(true, true, _retentionState, _warning);
            }
            if (_retentionState != AudioCacheRetentionState.Enabled)
            {
                return new(false, false, _retentionState, _warning);
            }
            try
            {
                using MemoryStream source = new(payload.ToArray(), writable: false);
                AudioCachePackPublishResult result = _packStore.Publish(
                    key,
                    source,
                    payload.Length,
                    segmentCompleted: true);
                _reusableBytes = _packStore.LiveBytes;
                if (result.QuotaFull)
                {
                    DisableRetention(
                        AudioCacheRetentionState.DisabledByQuota,
                        "Reusable audio cache live-byte quota is full; new entries will be rendered without retention.");
                }
                return new(result.Published, result.AlreadyPresent, _retentionState, _warning);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                DisableRetention(
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    "Reusable audio Pack writes failed; new cache misses will be rendered without retention. "
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
            if (_packStore.TryRead(key, out payload))
            {
                return true;
            }
            CapturePackCorruptionWarning();
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
            bool copied = _packStore.TryCopy(key, destination, out payloadLength);
            if (!copied)
            {
                CapturePackCorruptionWarning();
            }
            if (copied)
            {
                return true;
            }
            return TryCopyPendingPublish(key, destination, out payloadLength);
        }
    }

    public ReusableAudioReadLease? AcquireReusableReadLease(
        IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Dictionary<string, ReusableAudioReadEntry> entries = new(
                StringComparer.Ordinal);
            foreach (string key in keys.Distinct(StringComparer.Ordinal))
            {
                ValidateKey(key);
                if (_packStore.TryGetReadEntry(key, out ReusableAudioReadEntry? packEntry))
                {
                    entries.Add(key, packEntry!);
                    continue;
                }
                if (_pendingPublishes.TryGetValue(key, out PendingPublishSlice pending))
                {
                    entries.Add(
                        key,
                        new ReusableAudioReadEntry(
                            key,
                            pending.PayloadLength,
                            [new ReusableAudioReadExtent(
                                ReusableAudioReadExtentKind.RawPayload,
                                pending.Path,
                                LogicalOffset: 0,
                                pending.PayloadOffset,
                                pending.PayloadLength,
                                BlockIndex: 0,
                                BlockCount: 1)]));
                }
            }
            if (entries.Count == 0)
            {
                return null;
            }
            string[] leasedPendingPaths = entries.Values
                .SelectMany(static entry => entry.Extents)
                .Where(static extent => extent.Kind == ReusableAudioReadExtentKind.RawPayload)
                .Select(static extent => extent.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string path in leasedPendingPaths)
            {
                _pendingReadLeaseCounts.TryGetValue(path, out int count);
                _pendingReadLeaseCounts[path] = checked(count + 1);
            }
            _activeReusableReadLeaseCount++;
            return new ReusableAudioReadLease(
                entries,
                () => ReleaseReusableReadLease(leasedPendingPaths));
        }
    }

    public void QueueReusableBatch(
        AudioRecoverySpool spool,
        IReadOnlyList<AudioCachePublishSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(slices);
        AudioCachePublishSlice[] accepted;
        try
        {
            spool.ReleaseFileHandleForExternalUse();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed || _publishStopRequested, this);
                List<AudioCachePublishSlice> candidates = new(slices.Count);
                HashSet<string> batchKeys = new(StringComparer.Ordinal);
                foreach (AudioCachePublishSlice slice in slices)
                {
                    ValidateKey(slice.Key);
                    if (slice.PayloadOffset < 0
                        || slice.PayloadLength < 0
                        || slice.PayloadOffset > spool.LengthBytes - slice.PayloadLength)
                    {
                        throw new ArgumentOutOfRangeException(nameof(slices));
                    }
                    if (!batchKeys.Add(slice.Key)
                        || _packStore.Contains(slice.Key)
                        || _pendingPublishes.ContainsKey(slice.Key))
                    {
                        continue;
                    }
                    long recordLength = AudioCachePackStore.ComputeRecordLength(
                        slice.PayloadLength);
                    if (_retentionState != AudioCacheRetentionState.Enabled
                        || recordLength > _maximumReusableBytes
                            - _packStore.LiveBytes
                            - _pendingPublishBytes)
                    {
                        DisableRetention(
                            AudioCacheRetentionState.DisabledByQuota,
                            "Reusable audio cache live-byte quota is full; new entries will be rendered without retention.");
                        break;
                    }
                    candidates.Add(slice);
                    _pendingPublishBytes = checked(_pendingPublishBytes + recordLength);
                }
                accepted = candidates.ToArray();
                if (accepted.Length != 0)
                {
                    PendingPublishBatch batch = new(spool, accepted);
                    foreach (AudioCachePublishSlice slice in accepted)
                    {
                        _pendingPublishes.Add(
                            slice.Key,
                            new PendingPublishSlice(
                                spool.Path,
                                slice.PayloadOffset,
                                slice.PayloadLength));
                    }
                    _publishQueue.Enqueue(batch);
                    Monitor.PulseAll(_sync);
                }
            }
        }
        catch
        {
            spool.Dispose();
            throw;
        }
        if (accepted.Length == 0)
        {
            spool.Dispose();
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
            if (_packStore.Contains(key))
            {
                return new(true, true, _retentionState, _warning);
            }
            if (_retentionState != AudioCacheRetentionState.Enabled)
            {
                return new(false, false, _retentionState, _warning);
            }

            try
            {
                AudioCachePackPublishResult result = _packStore.Publish(
                    key,
                    source,
                    payloadLength,
                    segmentCompleted: true);
                _reusableBytes = _packStore.LiveBytes;
                if (result.QuotaFull)
                {
                    DisableRetention(
                        AudioCacheRetentionState.DisabledByQuota,
                        "Reusable audio cache live-byte quota is full; new entries will be rendered without retention.");
                }
                return new(result.Published, result.AlreadyPresent, _retentionState, _warning);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                DisableRetention(
                    AudioCacheRetentionState.DisabledByWriteFailure,
                    "Reusable audio Pack writes failed; new cache misses will be rendered without retention. "
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
            _packStore.Invalidate(key);
            _reusableBytes = _packStore.LiveBytes;
            _warning = new(
                AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
                "An invalid reusable audio PCM payload was isolated and will be rebuilt.");
        }
    }

    public void RegisterReusableGeneration(string owner, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _packStore.RegisterGeneration(owner, key);
            _reusableBytes = _packStore.LiveBytes;
        }
    }

    public void RegisterReusableGenerations(
        IReadOnlyList<AudioCacheGenerationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _packStore.RegisterGenerations(bindings);
            _reusableBytes = _packStore.LiveBytes;
        }
    }

    public bool TryCompactReusable(bool isStopped, TimeSpan idleDuration)
    {
        if (idleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleDuration));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_publishStopRequested)
            {
                return false;
            }
            _compactionRequested = true;
            _compactionStopped |= isStopped;
            if (idleDuration > _compactionIdleDuration)
            {
                _compactionIdleDuration = idleDuration;
            }
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    public AudioRecoverySpool CreateRecoverySpool(long lengthBytes, bool sparse = false)
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
                    if (sparse && OperatingSystem.IsWindows())
                    {
                        MarkSparse(stream);
                    }
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
            if (_disposed || _publishStopRequested)
            {
                return;
            }
            _publishStopRequested = true;
            Monitor.PulseAll(_sync);
        }
        _publishThread.Join();
        AudioRecoverySpool[] deferredSpools;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            deferredSpools = _deferredPendingSpools.Values.ToArray();
            _deferredPendingSpools.Clear();
        }
        foreach (AudioRecoverySpool spool in deferredSpools)
        {
            spool.Dispose();
        }
        _packStore.Dispose();
        _activeLock.Dispose();
        TryDeleteOwnedSession(_rootPath, _sessionPath);
        GC.SuppressFinalize(this);
    }

    private void RunPublishQueue()
    {
        while (true)
        {
            PendingPublishBatch? batch = null;
            bool compact = false;
            bool compactStopped = false;
            TimeSpan compactIdle = TimeSpan.Zero;
            lock (_sync)
            {
                while (_publishQueue.Count == 0
                    && (!_compactionRequested || _activeReusableReadLeaseCount != 0)
                    && !_publishStopRequested)
                {
                    Monitor.Wait(_sync);
                }
                if (_publishQueue.Count != 0)
                {
                    batch = _publishQueue.Dequeue();
                }
                else if (_compactionRequested && _activeReusableReadLeaseCount == 0)
                {
                    compact = true;
                    compactStopped = _compactionStopped;
                    compactIdle = _compactionIdleDuration;
                    _compactionRequested = false;
                    _compactionStopped = false;
                    _compactionIdleDuration = TimeSpan.Zero;
                }
                else if (_publishStopRequested)
                {
                    return;
                }
            }

            if (compact)
            {
                try
                {
                    _ = _packStore.TryCompact(compactStopped, compactIdle);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
                {
                    lock (_sync)
                    {
                        DisableRetention(
                            AudioCacheRetentionState.DisabledByWriteFailure,
                            "Audio cache Pack compaction failed; new cache misses will be rendered without retention. "
                                + exception.Message);
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _reusableBytes = _packStore.LiveBytes;
                    }
                }
                continue;
            }
            PendingPublishBatch activeBatch = batch
                ?? throw new InvalidOperationException(
                    "The audio Pack writer woke without queued work.");

            try
            {
                using FileStream source = new(
                    activeBatch.Spool.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                foreach (AudioCachePublishSlice slice in activeBatch.Slices)
                {
                    source.Position = slice.PayloadOffset;
                    _ = PublishReusable(slice.Key, source, slice.PayloadLength);
                    CompletePendingPublish(slice);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ObjectDisposedException)
            {
                lock (_sync)
                {
                    if (!_disposed)
                    {
                        DisableRetention(
                            AudioCacheRetentionState.DisabledByWriteFailure,
                            "Reusable audio Pack background publishing failed; new cache misses will be rendered without retention. "
                                + exception.Message);
                    }
                }
            }
            finally
            {
                foreach (AudioCachePublishSlice slice in activeBatch.Slices)
                {
                    CompletePendingPublish(slice);
                }
                DeferOrDisposePublishedSpool(activeBatch.Spool);
            }
        }
    }

    private void DeferOrDisposePublishedSpool(AudioRecoverySpool spool)
    {
        bool defer;
        lock (_sync)
        {
            defer = _pendingReadLeaseCounts.TryGetValue(spool.Path, out int count)
                && count != 0;
            if (defer)
            {
                _deferredPendingSpools.Add(spool.Path, spool);
            }
        }
        if (!defer)
        {
            spool.Dispose();
        }
    }

    private void ReleaseReusableReadLease(string[] paths)
    {
        List<AudioRecoverySpool>? release = null;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            if (_activeReusableReadLeaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "A reusable-audio read lease was released without an active owner.");
            }
            _activeReusableReadLeaseCount--;
            foreach (string path in paths)
            {
                if (!_pendingReadLeaseCounts.TryGetValue(path, out int count))
                {
                    continue;
                }
                if (count > 1)
                {
                    _pendingReadLeaseCounts[path] = count - 1;
                    continue;
                }
                _pendingReadLeaseCounts.Remove(path);
                if (_deferredPendingSpools.Remove(path, out AudioRecoverySpool? spool))
                {
                    (release ??= []).Add(spool);
                }
            }
            Monitor.PulseAll(_sync);
        }
        if (release is not null)
        {
            foreach (AudioRecoverySpool spool in release)
            {
                spool.Dispose();
            }
        }
    }

    private void CompletePendingPublish(AudioCachePublishSlice slice)
    {
        lock (_sync)
        {
            if (_pendingPublishes.Remove(slice.Key))
            {
                _pendingPublishBytes = Math.Max(
                    0,
                    _pendingPublishBytes
                        - AudioCachePackStore.ComputeRecordLength(slice.PayloadLength));
            }
        }
    }

    private bool TryCopyPendingPublish(
        string key,
        Stream destination,
        out long payloadLength)
    {
        payloadLength = 0;
        if (!_pendingPublishes.TryGetValue(key, out PendingPublishSlice pending))
        {
            return false;
        }
        try
        {
            using FileStream source = new(
                pending.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan);
            source.Position = pending.PayloadOffset;
            byte[] buffer = new byte[128 * 1024];
            long remaining = pending.PayloadLength;
            while (remaining != 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = source.Read(buffer, 0, requested);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "A pending reusable audio payload is truncated.");
                }
                destination.Write(buffer, 0, read);
                remaining -= read;
            }
            payloadLength = pending.PayloadLength;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            DisableRetention(
                AudioCacheRetentionState.DisabledByWriteFailure,
                "A completed pending audio cache payload could not be read. "
                    + exception.Message);
            return false;
        }
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

    private void CapturePackCorruptionWarning()
    {
        if (_packStore.ConsumeCorruptionDetected())
        {
            _warning = new(
                AudioCacheWarningCode.AudioCacheCorruptAndRebuilt,
                "A corrupt reusable audio Pack record was isolated and will be rebuilt.");
        }
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


    private static void MarkSparse(FileStream stream)
    {
        const uint FsctlSetSparse = 0x000900c4;
        if (!DeviceIoControl(
            stream.SafeFileHandle.DangerousGetHandle(),
            FsctlSetSparse,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero))
        {
            throw new IOException(
                "The Segment cache staging file could not be marked sparse.",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    private readonly record struct PendingPublishSlice(
        string Path,
        long PayloadOffset,
        long PayloadLength);

    private sealed record PendingPublishBatch(
        AudioRecoverySpool Spool,
        AudioCachePublishSlice[] Slices);

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
