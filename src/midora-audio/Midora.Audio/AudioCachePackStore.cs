using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Midora.Audio;

internal sealed class AudioCachePackStore : IDisposable
{
    internal const long MaximumGenerationBytes = 2L * 1024 * 1024 * 1024;
    internal const long MinimumCompactionDeadBytes = 256L * 1024 * 1024;
    internal const double MinimumCompactionDeadRatio = 0.35;
    internal const long MinimumCompactionHeadroomBytes = 4L * 1024 * 1024 * 1024;
    internal const uint PackMagic = 0x5041434d; // MCAP
    internal const uint EntryMagic = 0x4541434d; // MCAE
    private const uint IndexMagic = 0x5849434d; // MCIX
    internal const int Version = 2;
    internal const int PackHeaderSize = 16;
    internal const int BlockPayloadBytes =
        RollingAudioPreparationPolicy.SegmentBlockFrameCount * 2 * sizeof(float);
    internal const int EntryHeaderSize = 4 + 4 + 4 + 4 + 8 + 4 + 4 + 32 + 32;
    private const long GroupCommitBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan GroupCommitInterval = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _indexPath;
    private readonly long _maximumLiveBytes;
    private readonly long _maximumGenerationBytes;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _generationByOwner = new(StringComparer.Ordinal);
    private readonly HashSet<string> _managedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveKeys = new(StringComparer.Ordinal);
    private int _currentGeneration = 1;
    private long _physicalBytes;
    private long _liveBytes;
    private long _pendingCommitBytes;
    private readonly HashSet<int> _pendingCommitGenerations = [];
    private long _lastCommitTimestamp;
    private int _corruptionDetected;
    private bool _disposed;

    public AudioCachePackStore(string directory, long maximumLiveBytes)
        : this(directory, maximumLiveBytes, MaximumGenerationBytes)
    {
    }

    internal AudioCachePackStore(
        string directory,
        long maximumLiveBytes,
        long maximumGenerationBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumLiveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLiveBytes));
        }
        if (maximumGenerationBytes <= PackHeaderSize + EntryHeaderSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGenerationBytes));
        }
        _directory = Path.GetFullPath(directory);
        _indexPath = Path.Combine(_directory, "pack-index.mcix");
        _maximumLiveBytes = maximumLiveBytes;
        _maximumGenerationBytes = maximumGenerationBytes;
        Directory.CreateDirectory(_directory);
        CreatePack(_currentGeneration);
        _physicalBytes = PackHeaderSize;
        _lastCommitTimestamp = Stopwatch.GetTimestamp();
        WriteIndexCheckpoint();
    }

    public long LiveBytes
    {
        get { lock (_sync) { return _liveBytes; } }
    }

    public long PhysicalBytes
    {
        get { lock (_sync) { return _physicalBytes; } }
    }

    public long DeadBytes
    {
        get
        {
            lock (_sync)
            {
                return Math.Max(
                    0,
                    _physicalBytes - (PackHeaderSize * PackCount) - _liveBytes);
            }
        }
    }

    public int PackCount
    {
        get
        {
            lock (_sync)
            {
                return Directory.EnumerateFiles(_directory, "pack-*.mcap").Count();
            }
        }
    }

    public bool ConsumeCorruptionDetected() =>
        Interlocked.Exchange(ref _corruptionDetected, 0) != 0;

    internal static long ComputeRecordLength(long payloadLength)
    {
        if (payloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }
        long blockCount = ComputeBlockCount(payloadLength);
        return checked(payloadLength + (blockCount * EntryHeaderSize));
    }

    internal static long ComputeBlockCount(long payloadLength)
    {
        if (payloadLength <= AudioPcmCachePayload.HeaderByteCount)
        {
            return 1;
        }
        long pcmBytes = payloadLength - AudioPcmCachePayload.HeaderByteCount;
        return checked(
            1
            + (pcmBytes / BlockPayloadBytes)
            + (pcmBytes % BlockPayloadBytes == 0 ? 0 : 1));
    }

    private static int ComputeBlockPayloadLength(
        long payloadLength,
        int blockIndex,
        long remaining)
    {
        if (blockIndex == 0)
        {
            return (int)Math.Min(
                AudioPcmCachePayload.HeaderByteCount,
                payloadLength);
        }
        return (int)Math.Min(BlockPayloadBytes, remaining);
    }

    public bool Contains(string key)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _entries.ContainsKey(key);
        }
    }

    public bool TryRead(string key, out byte[] payload)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out Entry entry)
                || entry.PayloadLength > int.MaxValue)
            {
                payload = [];
                return false;
            }
            payload = new byte[(int)entry.PayloadLength];
            using MemoryStream destination = new(payload, writable: true);
            if (TryCopyCore(key, entry, destination, out _))
            {
                return true;
            }
            payload = [];
            return false;
        }
    }

    public bool TryCopy(string key, Stream destination, out long payloadLength)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The cache destination must be writable.", nameof(destination));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out Entry entry))
            {
                payloadLength = 0;
                return false;
            }
            return TryCopyCore(key, entry, destination, out payloadLength);
        }
    }

    public bool TryGetReadEntry(string key, out ReusableAudioReadEntry? readEntry)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out Entry entry))
            {
                readEntry = null;
                return false;
            }
            long logicalOffset = 0;
            ReusableAudioReadExtent[] extents = new ReusableAudioReadExtent[entry.Extents.Length];
            for (int index = 0; index < entry.Extents.Length; index++)
            {
                Extent extent = entry.Extents[index];
                extents[index] = new(
                    ReusableAudioReadExtentKind.PackBlock,
                    GetPackPath(extent.Generation),
                    logicalOffset,
                    extent.RecordOffset,
                    extent.PayloadLength,
                    index,
                    entry.Extents.Length);
                logicalOffset = checked(logicalOffset + extent.PayloadLength);
            }
            readEntry = new(key, entry.PayloadLength, extents);
            return true;
        }
    }

    public AudioCachePackPublishResult Publish(
        string key,
        Stream source,
        long payloadLength,
        bool segmentCompleted)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The cache source must be readable.", nameof(source));
        }
        if (payloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.ContainsKey(key))
            {
                return new(true, true, false);
            }
            if (_managedKeys.Contains(key)
                && !_generationByOwner.Values.Contains(key, StringComparer.Ordinal))
            {
                return new(false, false, false);
            }
            long recordLength = ComputeRecordLength(payloadLength);
            if (recordLength > _maximumLiveBytes - _liveBytes)
            {
                return new(false, false, true);
            }
            int blockCount = checked((int)ComputeBlockCount(payloadLength));
            byte[] keyBytes = Convert.FromHexString(key);
            byte[] buffer = new byte[BlockPayloadBytes];
            byte[] headerBuffer = new byte[EntryHeaderSize];
            Extent[] extents = new Extent[blockCount];
            long remaining = payloadLength;
            for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
            {
                int blockPayloadLength = ComputeBlockPayloadLength(
                    payloadLength,
                    blockIndex,
                    remaining);
                int readOffset = 0;
                while (readOffset < blockPayloadLength)
                {
                    int read = source.Read(
                        buffer,
                        readOffset,
                        blockPayloadLength - readOffset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException(
                            "The reusable audio cache source is truncated.");
                    }
                    readOffset += read;
                }
                byte[] digest = SHA256.HashData(
                    buffer.AsSpan(0, blockPayloadLength));
                long blockRecordLength = checked(EntryHeaderSize + blockPayloadLength);
                EnsureGenerationCapacity(blockRecordLength);
                string packPath = GetPackPath(_currentGeneration);
                using FileStream pack = new(
                    packPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                long recordOffset = pack.Length;
                try
                {
                    pack.Position = recordOffset;
                    Span<byte> header = headerBuffer;
                    header.Clear();
                    BinaryPrimitives.WriteUInt32LittleEndian(header, EntryMagic);
                    BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
                    BinaryPrimitives.WriteInt32LittleEndian(header[8..], blockIndex);
                    BinaryPrimitives.WriteInt32LittleEndian(header[12..], blockCount);
                    BinaryPrimitives.WriteInt64LittleEndian(header[16..], payloadLength);
                    BinaryPrimitives.WriteInt32LittleEndian(header[24..], blockPayloadLength);
                    keyBytes.CopyTo(header[32..64]);
                    digest.CopyTo(header[64..96]);
                    pack.Write(header);
                    pack.Write(buffer, 0, blockPayloadLength);
                }
                catch
                {
                    pack.SetLength(recordOffset);
                    throw;
                }
                extents[blockIndex] = new(
                    _currentGeneration,
                    recordOffset,
                    blockPayloadLength,
                    blockRecordLength);
                _physicalBytes = checked(_physicalBytes + blockRecordLength);
                _pendingCommitBytes = checked(
                    _pendingCommitBytes + blockRecordLength);
                _pendingCommitGenerations.Add(_currentGeneration);
                remaining -= blockPayloadLength;
                if (segmentCompleted && blockIndex == blockCount - 1
                    || _pendingCommitBytes >= GroupCommitBytes
                    || Stopwatch.GetElapsedTime(_lastCommitTimestamp) >= GroupCommitInterval)
                {
                    pack.Flush(flushToDisk: false);
                    FlushPendingGenerations();
                }
            }

            Entry entry = new(payloadLength, recordLength, extents);
            _entries.Add(key, entry);
            _liveKeys.Add(key);
            _liveBytes = checked(_liveBytes + recordLength);
            WriteIndexCheckpoint();
            return new(true, false, false);
        }
    }

    internal AudioCachePackJournalAdoptionResult AdoptJournalDirectory(
        string journalDirectory,
        IReadOnlyCollection<string> completedKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        ArgumentNullException.ThrowIfNull(completedKeys);
        string directory = Path.GetFullPath(journalDirectory);
        HashSet<string> completed = new(completedKeys, StringComparer.Ordinal);
        foreach (string key in completed)
        {
            ValidateKey(key);
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string[] journalPaths = Directory.Exists(directory)
                ? Directory.EnumerateFiles(
                        directory,
                        AudioCachePackJournal.JournalFilePattern)
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToArray()
                : [];
            if (journalPaths.Length == 0 || completed.Count == 0)
            {
                return new(0, 0, false);
            }

            Dictionary<string, JournalEntryBuilder> builders = new(StringComparer.Ordinal);
            for (int fileIndex = 0; fileIndex < journalPaths.Length; fileIndex++)
            {
                ScanJournal(journalPaths[fileIndex], fileIndex, completed, builders);
            }

            List<JournalEntryBuilder> accepted = [];
            long addedLiveBytes = 0;
            foreach (string key in completed.OrderBy(static value => value, StringComparer.Ordinal))
            {
                if (_entries.ContainsKey(key)
                    || _managedKeys.Contains(key)
                        && !_generationByOwner.Values.Contains(key, StringComparer.Ordinal))
                {
                    continue;
                }
                if (!builders.TryGetValue(key, out JournalEntryBuilder? builder)
                    || !builder.IsComplete)
                {
                    throw new InvalidDataException(
                        "A completed Segment PCM journal entry is incomplete.");
                }
                accepted.Add(builder);
                addedLiveBytes = checked(addedLiveBytes + builder.RecordLength);
            }
            if (accepted.Count == 0)
            {
                return new(0, 0, false);
            }
            if (addedLiveBytes > _maximumLiveBytes - _liveBytes)
            {
                return new(0, 0, true);
            }

            HashSet<int> referencedFiles = accepted
                .SelectMany(static entry => entry.Extents)
                .Select(static extent => extent.FileIndex)
                .ToHashSet();
            Dictionary<int, int> adoptedGenerations = [];
            long addedPhysicalBytes = 0;
            foreach (int fileIndex in referencedFiles.Order())
            {
                string sourcePath = journalPaths[fileIndex];
                int generation = checked(++_currentGeneration);
                RewritePackGeneration(sourcePath, generation);
                string destinationPath = GetPackPath(generation);
                File.Move(sourcePath, destinationPath);
                adoptedGenerations.Add(fileIndex, generation);
                addedPhysicalBytes = checked(
                    addedPhysicalBytes + new FileInfo(destinationPath).Length);
            }

            foreach (JournalEntryBuilder builder in accepted)
            {
                Extent[] extents = new Extent[builder.Extents.Length];
                for (int blockIndex = 0; blockIndex < extents.Length; blockIndex++)
                {
                    JournalExtent source = builder.Extents[blockIndex];
                    extents[blockIndex] = new(
                        adoptedGenerations[source.FileIndex],
                        source.RecordOffset,
                        source.PayloadLength,
                        source.RecordLength);
                }
                _entries.Add(
                    builder.Key,
                    new Entry(builder.PayloadLength, builder.RecordLength, extents));
                _liveKeys.Add(builder.Key);
            }
            _liveBytes = checked(_liveBytes + addedLiveBytes);
            _physicalBytes = checked(_physicalBytes + addedPhysicalBytes);
            WriteIndexCheckpoint();
            return new(accepted.Count, addedLiveBytes, false);
        }
    }

    public void RegisterGeneration(string owner, string key)
    {
        RegisterGenerations([new AudioCacheGenerationBinding(owner, key)]);
    }

    public void RegisterGenerations(
        IReadOnlyList<AudioCacheGenerationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (AudioCacheGenerationBinding binding in bindings)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(binding.Owner);
                ValidateKey(binding.Key);
                _managedKeys.Add(binding.Key);
                if (_generationByOwner.TryGetValue(
                        binding.Owner,
                        out string? previous)
                    && !string.Equals(previous, binding.Key, StringComparison.Ordinal))
                {
                    _generationByOwner[binding.Owner] = binding.Key;
                    if (!_generationByOwner.Values.Contains(previous, StringComparer.Ordinal)
                        && _liveKeys.Remove(previous)
                        && _entries.TryGetValue(previous, out Entry stale))
                    {
                        _liveBytes -= stale.RecordLength;
                    }
                }
                else
                {
                    _generationByOwner[binding.Owner] = binding.Key;
                }
                if (_entries.TryGetValue(binding.Key, out Entry current)
                    && _liveKeys.Add(binding.Key))
                {
                    _liveBytes = checked(_liveBytes + current.RecordLength);
                }
            }
            WriteIndexCheckpoint();
        }
    }

    public void Invalidate(string key)
    {
        ValidateKey(key);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.Remove(key, out Entry entry) && _liveKeys.Remove(key))
            {
                _liveBytes -= entry.RecordLength;
            }
            foreach (string owner in _generationByOwner
                .Where(value => string.Equals(value.Value, key, StringComparison.Ordinal))
                .Select(value => value.Key)
                .ToArray())
            {
                _generationByOwner.Remove(owner);
            }
            _managedKeys.Remove(key);
            WriteIndexCheckpoint();
        }
    }

    public bool TryCompact(bool isStopped, TimeSpan idleDuration) =>
        TryCompact(
            isStopped,
            idleDuration,
            MinimumCompactionDeadBytes,
            MinimumCompactionDeadRatio,
            MinimumCompactionHeadroomBytes);

    internal bool TryCompact(
        bool isStopped,
        TimeSpan idleDuration,
        long minimumDeadBytes,
        double minimumDeadRatio,
        long minimumHeadroomBytes)
    {
        if (minimumDeadBytes < 0
            || minimumDeadRatio is < 0 or > 1
            || minimumHeadroomBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumDeadBytes));
        }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long deadBytes = Math.Max(0, _physicalBytes - (PackHeaderSize * PackCount) - _liveBytes);
            long recordBytes = Math.Max(1, _physicalBytes - (PackHeaderSize * PackCount));
            if ((!isStopped && idleDuration < TimeSpan.FromSeconds(10))
                || idleDuration < TimeSpan.Zero
                || deadBytes < minimumDeadBytes
                || deadBytes / (double)recordBytes < minimumDeadRatio)
            {
                return false;
            }
            string root = Path.GetPathRoot(_directory)
                ?? throw new IOException("The cache directory has no volume root.");
            if (new DriveInfo(root).AvailableFreeSpace
                < checked(_liveBytes + minimumHeadroomBytes))
            {
                return false;
            }

            string temporaryDirectory = Path.Combine(
                _directory,
                $".compact-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            try
            {
                Dictionary<string, Entry> compacted = new(StringComparer.Ordinal);
                int generation = checked(_currentGeneration + 1);
                long generationLength = 0;
                FileStream? destination = null;
                try
                {
                    foreach (string key in _liveKeys.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        if (!_entries.TryGetValue(key, out Entry sourceEntry))
                        {
                            continue;
                        }
                        Extent[] copiedExtents = new Extent[sourceEntry.Extents.Length];
                        for (int extentIndex = 0;
                            extentIndex < sourceEntry.Extents.Length;
                            extentIndex++)
                        {
                            Extent sourceExtent = sourceEntry.Extents[extentIndex];
                            if (destination is null
                                || generationLength + sourceExtent.RecordLength
                                    > _maximumGenerationBytes)
                            {
                                destination?.Flush(flushToDisk: true);
                                destination?.Dispose();
                                if (destination is not null)
                                {
                                    generation++;
                                }
                                string path = Path.Combine(
                                    temporaryDirectory,
                                    GetPackFileName(generation));
                                destination = CreatePackFile(path, generation);
                                generationLength = PackHeaderSize;
                            }
                            long offset = destination.Position;
                            CopyRecord(sourceExtent, destination);
                            copiedExtents[extentIndex] = sourceExtent with
                            {
                                Generation = generation,
                                RecordOffset = offset
                            };
                            generationLength += sourceExtent.RecordLength;
                        }
                        compacted.Add(
                            key,
                            new Entry(
                                sourceEntry.PayloadLength,
                                sourceEntry.RecordLength,
                                copiedExtents));
                    }
                    destination?.Flush(flushToDisk: true);
                }
                finally
                {
                    destination?.Dispose();
                }

                string[] oldPacks = Directory
                    .EnumerateFiles(_directory, "pack-*.mcap")
                    .ToArray();
                string[] newPacks = Directory
                    .EnumerateFiles(temporaryDirectory, "pack-*.mcap")
                    .ToArray();
                foreach (string newPack in newPacks)
                {
                    File.Move(newPack, Path.Combine(_directory, Path.GetFileName(newPack)));
                }
                _entries.Clear();
                foreach ((string key, Entry value) in compacted)
                {
                    _entries.Add(key, value);
                }
                _currentGeneration = compacted.Count == 0
                    ? checked(_currentGeneration + 1)
                    : compacted.Values
                        .SelectMany(value => value.Extents)
                        .Max(value => value.Generation);
                if (compacted.Count == 0)
                {
                    CreatePack(_currentGeneration);
                }
                // Publish the new index while every old generation still exists.
                // A process failure before this checkpoint leaves the old index and
                // old Packs usable; a failure after it leaves only harmless old Packs.
                WriteIndexCheckpoint();
                foreach (string oldPack in oldPacks)
                {
                    File.Delete(oldPack);
                }
                _physicalBytes = Directory
                    .EnumerateFiles(_directory, "pack-*.mcap")
                    .Sum(path => new FileInfo(path).Length);
                return true;
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed || _pendingCommitBytes == 0)
            {
                return;
            }
            FlushPendingGenerations();
        }
    }

    private void FlushPendingGenerations()
    {
        foreach (int generation in _pendingCommitGenerations)
        {
            using FileStream pack = new(
                GetPackPath(generation),
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.WriteThrough);
            pack.Flush(flushToDisk: true);
        }
        _pendingCommitGenerations.Clear();
        _pendingCommitBytes = 0;
        _lastCommitTimestamp = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            Flush();
            _disposed = true;
        }
    }

    private bool TryCopyCore(
        string key,
        Entry entry,
        Stream destination,
        out long payloadLength)
    {
        payloadLength = 0;
        long destinationStart = destination.CanSeek ? destination.Position : -1;
        try
        {
            byte[] keyBytes = Convert.FromHexString(key);
            byte[] buffer = new byte[BlockPayloadBytes];
            byte[] headerBuffer = new byte[EntryHeaderSize];
            byte[] digestBuffer = new byte[32];
            long copied = 0;
            for (int blockIndex = 0; blockIndex < entry.Extents.Length; blockIndex++)
            {
                Extent extent = entry.Extents[blockIndex];
                using FileStream pack = new(
                    GetPackPath(extent.Generation),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 128 * 1024,
                    FileOptions.RandomAccess);
                pack.Position = extent.RecordOffset;
                Span<byte> header = headerBuffer;
                pack.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntryMagic
                    || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version
                    || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != blockIndex
                    || BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != entry.Extents.Length
                    || BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != entry.PayloadLength
                    || BinaryPrimitives.ReadInt32LittleEndian(header[24..]) != extent.PayloadLength
                    || BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0
                    || !CryptographicOperations.FixedTimeEquals(header[32..64], keyBytes))
                {
                    throw new InvalidDataException(
                        "The reusable audio Pack block header is invalid.");
                }
                pack.ReadExactly(buffer.AsSpan(0, extent.PayloadLength));
                Span<byte> digest = digestBuffer;
                SHA256.HashData(buffer.AsSpan(0, extent.PayloadLength), digest);
                if (!CryptographicOperations.FixedTimeEquals(digest, header[64..96]))
                {
                    throw new InvalidDataException(
                        "The reusable audio Pack block checksum is invalid.");
                }
                destination.Write(buffer, 0, extent.PayloadLength);
                copied = checked(copied + extent.PayloadLength);
            }
            if (copied != entry.PayloadLength)
            {
                throw new InvalidDataException(
                    "The reusable audio Pack extent length is invalid.");
            }
            payloadLength = entry.PayloadLength;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or CryptographicException)
        {
            if (destinationStart >= 0)
            {
                destination.Position = destinationStart;
                destination.SetLength(destinationStart);
            }
            InvalidateCore(key);
            return false;
        }
    }

    private void CopyRecord(Extent sourceExtent, Stream destination)
    {
        using FileStream source = new(
            GetPackPath(sourceExtent.Generation),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        source.Position = sourceExtent.RecordOffset;
        byte[] buffer = new byte[128 * 1024];
        long remaining = sourceExtent.RecordLength;
        while (remaining != 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new EndOfStreamException("A live Pack record is truncated during compaction.");
            }
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private void InvalidateCore(string key)
    {
        if (_entries.Remove(key, out Entry entry) && _liveKeys.Remove(key))
        {
            _liveBytes -= entry.RecordLength;
        }
        Volatile.Write(ref _corruptionDetected, 1);
        WriteIndexCheckpoint();
    }

    private void EnsureGenerationCapacity(long recordLength)
    {
        if (recordLength > _maximumGenerationBytes - PackHeaderSize)
        {
            throw new InvalidDataException(
                "A cache block record exceeds the configured Pack generation size.");
        }
        string current = GetPackPath(_currentGeneration);
        if (new FileInfo(current).Length + recordLength <= _maximumGenerationBytes)
        {
            return;
        }
        _currentGeneration++;
        CreatePack(_currentGeneration);
        _physicalBytes = checked(_physicalBytes + PackHeaderSize);
    }

    private void CreatePack(int generation) =>
        CreatePackFile(GetPackPath(generation), generation).Dispose();

    internal static FileStream CreatePackFile(string path, int generation)
    {
        FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[PackHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, PackMagic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], generation);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0);
        stream.Write(header);
        stream.Flush(flushToDisk: true);
        return stream;
    }

    private static void ScanJournal(
        string path,
        int fileIndex,
        HashSet<string> completedKeys,
        Dictionary<string, JournalEntryBuilder> builders)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        Span<byte> packHeader = stackalloc byte[PackHeaderSize];
        stream.ReadExactly(packHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(packHeader) != PackMagic
            || BinaryPrimitives.ReadInt32LittleEndian(packHeader[4..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(packHeader[8..]) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(packHeader[12..]) != 0)
        {
            throw new InvalidDataException("A reusable audio Pack journal header is invalid.");
        }

        byte[] entryHeaderBuffer = new byte[EntryHeaderSize];
        while (stream.Position != stream.Length)
        {
            long recordOffset = stream.Position;
            if (stream.Length - recordOffset < EntryHeaderSize)
            {
                throw new InvalidDataException("A reusable audio Pack journal record is truncated.");
            }
            Span<byte> header = entryHeaderBuffer;
            stream.ReadExactly(header);
            int blockIndex = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            int blockPayloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != EntryMagic
                || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version
                || BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 0
                || payloadLength <= 0
                || blockCount != ComputeBlockCount(payloadLength)
                || blockIndex < 0
                || blockIndex >= blockCount
                || blockPayloadLength != ComputeExpectedBlockPayloadLength(
                    payloadLength,
                    blockIndex)
                || blockPayloadLength > stream.Length - stream.Position)
            {
                throw new InvalidDataException("A reusable audio Pack journal record header is invalid.");
            }
            string key = Convert.ToHexStringLower(header[32..64]);
            if (completedKeys.Contains(key))
            {
                if (!builders.TryGetValue(key, out JournalEntryBuilder? builder))
                {
                    builder = new(key, payloadLength, blockCount);
                    builders.Add(key, builder);
                }
                builder.Add(
                    blockIndex,
                    payloadLength,
                    blockCount,
                    new(
                        fileIndex,
                        recordOffset,
                        blockPayloadLength,
                        checked(EntryHeaderSize + blockPayloadLength)));
            }
            stream.Position = checked(stream.Position + blockPayloadLength);
        }
    }

    private static int ComputeExpectedBlockPayloadLength(long payloadLength, int blockIndex)
    {
        if (blockIndex == 0)
        {
            return checked((int)Math.Min(AudioPcmCachePayload.HeaderByteCount, payloadLength));
        }
        long pcmOffset = checked((long)(blockIndex - 1) * BlockPayloadBytes);
        long remaining = checked(
            payloadLength - AudioPcmCachePayload.HeaderByteCount - pcmOffset);
        return remaining <= 0
            ? -1
            : checked((int)Math.Min(BlockPayloadBytes, remaining));
    }

    private static void RewritePackGeneration(string path, int generation)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        Span<byte> value = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(value, generation);
        stream.Position = 8;
        stream.Write(value);
        stream.Flush(flushToDisk: true);
    }

    private void WriteIndexCheckpoint()
    {
        string temporary = _indexPath + ".tmp";
        using (FileStream stream = new(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(IndexMagic);
            writer.Write(Version);
            writer.Write(_currentGeneration);
            writer.Write(_entries.Count);
            foreach ((string key, Entry entry) in _entries.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                writer.Write(Convert.FromHexString(key));
                writer.Write(entry.PayloadLength);
                writer.Write(entry.RecordLength);
                writer.Write(_liveKeys.Contains(key));
                writer.Write(new byte[3]);
                writer.Write(entry.Extents.Length);
                foreach (Extent extent in entry.Extents)
                {
                    writer.Write(extent.Generation);
                    writer.Write(extent.RecordOffset);
                    writer.Write(extent.PayloadLength);
                    writer.Write(extent.RecordLength);
                }
            }
            writer.Write(_generationByOwner.Count);
            foreach ((string owner, string key) in _generationByOwner.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                writer.Write(owner);
                writer.Write(Convert.FromHexString(key));
            }
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _indexPath, overwrite: true);
    }

    private string GetPackPath(int generation) =>
        Path.Combine(_directory, GetPackFileName(generation));

    private static string GetPackFileName(int generation) =>
        $"pack-{generation:D8}.mcap";

    internal static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 64
            || key.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "An audio cache key must be lowercase SHA-256 hexadecimal.",
                nameof(key));
        }
    }

    private readonly record struct Entry(
        long PayloadLength,
        long RecordLength,
        Extent[] Extents);

    private readonly record struct Extent(
        int Generation,
        long RecordOffset,
        int PayloadLength,
        long RecordLength);

    private sealed class JournalEntryBuilder
    {
        private long _recordLength;

        public JournalEntryBuilder(string key, long payloadLength, int blockCount)
        {
            Key = key;
            PayloadLength = payloadLength;
            Extents = new JournalExtent[blockCount];
        }

        public string Key { get; }
        public long PayloadLength { get; }
        public JournalExtent[] Extents { get; }
        public long RecordLength => _recordLength;
        public bool IsComplete => Extents.All(static value => value.RecordLength != 0);

        public void Add(
            int blockIndex,
            long payloadLength,
            int blockCount,
            JournalExtent extent)
        {
            if (payloadLength != PayloadLength
                || blockCount != Extents.Length
                || Extents[blockIndex].RecordLength != 0)
            {
                throw new InvalidDataException(
                    "A reusable audio Pack journal entry has inconsistent or duplicate blocks.");
            }
            Extents[blockIndex] = extent;
            _recordLength = checked(_recordLength + extent.RecordLength);
        }
    }

    private readonly record struct JournalExtent(
        int FileIndex,
        long RecordOffset,
        int PayloadLength,
        long RecordLength);
}

internal readonly record struct AudioCachePackPublishResult(
    bool Published,
    bool AlreadyPresent,
    bool QuotaFull);

internal readonly record struct AudioCachePackJournalAdoptionResult(
    int PublishedEntryCount,
    long PublishedLiveBytes,
    bool QuotaFull);
