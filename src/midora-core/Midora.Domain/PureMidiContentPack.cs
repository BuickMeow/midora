using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Midora.Domain;

public enum PureMidiContentRecordKind : byte
{
    Note = 1,
    ChannelEvent = 2,
    OpaqueEvent = 3,
    NoteOnEndpoint = 4,
    NoteOffEndpoint = 5,
    ChannelEventEndpoint = 6
}

public sealed class PureMidiContentPackWriter : IDisposable
{
    public const int MaximumPageRecordCount = 65_536;
    public const int MaximumEndpointPageRecordCount = 16_384;
    public const int MaximumDecodedPageByteCount = 4 * 1024 * 1024;

    private const int HeaderByteCount = 48;
    private const int DirectoryEntryByteCount = 116;
    private const int FooterByteCount = 48;
    private const int Version = 3;
    private static ReadOnlySpan<byte> HeaderMagic => "MIDMPK3\0"u8;
    private static ReadOnlySpan<byte> FooterMagic => "MIDMPKF\0"u8;

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly List<PageDescriptor> _pages = [];
    private readonly Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), PageBuilder> _builders = [];
    private readonly Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), int> _nextOrdinals = [];
    private bool _completed;
    private bool _disposed;
    private bool _streamsDisposed;

    public PureMidiContentPackWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _stream = new FileStream(
            _path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        _writer = new(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _writer.Write(new byte[HeaderByteCount]);
    }

    public string Path => _path;

    public void AddNote(MidoraId segmentId, DirectMidiNoteValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.Note).AddNote(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.NoteOnEndpoint).AddNote(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.NoteOffEndpoint).AddNote(value);
    }

    public void AddChannelEvent(MidoraId segmentId, DirectMidiChannelEventValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.ChannelEvent).AddChannelEvent(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.ChannelEventEndpoint)
            .AddChannelEvent(value);
    }

    public void AddOpaqueEvent(MidoraId segmentId, OpaqueMidiEventValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.OpaqueEvent).AddOpaqueEvent(value);
    }

    public PureMidiContentPack Complete(PureMidiContentPackDecodedCache? decodedCache = null)
    {
        ThrowIfUnavailable();
        foreach (PageBuilder builder in _builders.Values
            .OrderBy(value => value.SegmentId)
            .ThenBy(value => value.Kind))
        {
            Flush(builder);
        }

        long directoryOffset = _stream.Position;
        using MemoryStream directoryBuffer = new(checked(_pages.Count * DirectoryEntryByteCount));
        using (BinaryWriter directoryWriter = new(
            directoryBuffer,
            System.Text.Encoding.UTF8,
            leaveOpen: true))
        {
            foreach (PageDescriptor page in _pages) WriteDirectoryEntry(directoryWriter, page);
        }
        byte[] directoryBytes = directoryBuffer.ToArray();
        byte[] directoryHash = SHA256.HashData(directoryBytes);
        _writer.Write(directoryBytes);
        long footerOffset = _stream.Position;
        _writer.Write(FooterMagic);
        _writer.Write(directoryHash);
        _writer.Write(checked(footerOffset + FooterByteCount));
        _writer.Flush();

        _stream.Position = 0;
        _writer.Write(HeaderMagic);
        _writer.Write(Version);
        _writer.Write(HeaderByteCount);
        _writer.Write(_pages.Count);
        _writer.Write(0);
        _writer.Write(directoryOffset);
        _writer.Write((long)directoryBytes.Length);
        _writer.Write(footerOffset);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
        _completed = true;
        DisposeStreams();
        return decodedCache is null
            ? PureMidiContentPack.Open(_path)
            : PureMidiContentPack.Open(_path, decodedCache);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeStreams();
        if (!_completed)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // A failed transaction remains unpublished. Cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed transaction remains unpublished. Cleanup is best effort.
            }
        }
    }

    private PageBuilder GetBuilder(MidoraId segmentId, PureMidiContentRecordKind kind)
    {
        ThrowIfUnavailable();
        var key = (segmentId, kind);
        if (!_builders.TryGetValue(key, out PageBuilder? result))
        {
            result = new(segmentId, kind, Flush);
            _builders.Add(key, result);
        }
        return result;
    }

    private void Flush(PageBuilder builder)
    {
        if (builder.RecordCount == 0) return;
        ArraySegment<byte> decoded = builder.GetDecodedPage();
        if (decoded.Count > MaximumDecodedPageByteCount)
        {
            throw new InvalidDataException(
                $"A Pure MIDI content page decoded to {decoded.Count} bytes, exceeding {MaximumDecodedPageByteCount} bytes.");
        }
        long offset = _stream.Position;
        using (BrotliStream brotli = new(_stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(decoded.AsSpan());
        }
        int storedByteCount = checked((int)(_stream.Position - offset));
        var ordinalKey = (builder.SegmentId, builder.Kind);
        int firstOrdinal = _nextOrdinals.GetValueOrDefault(ordinalKey);
        _nextOrdinals[ordinalKey] = checked(firstOrdinal + builder.LastPageRecordCount);
        _pages.Add(new(
            builder.SegmentId,
            builder.Kind,
            firstOrdinal,
            builder.LastPageRecordCount,
            builder.LastPageMinimumTick,
            builder.LastPageMaximumTick,
            builder.LastPageMaximumActiveEndTick,
            builder.LastPageMinimumId,
            builder.LastPageMaximumId,
            builder.LastPageMinimumKey,
            builder.LastPageMaximumKey,
            offset,
            storedByteCount,
            decoded.Count,
            SHA256.HashData(decoded.AsSpan())));
        builder.ResetPage();
    }

    private static void WriteDirectoryEntry(BinaryWriter writer, PageDescriptor page)
    {
        writer.Write((byte)page.Kind);
        writer.Write(new byte[3]);
        writer.Write(page.SegmentId.Value);
        writer.Write(page.FirstOrdinal);
        writer.Write(page.RecordCount);
        writer.Write(page.MinimumTick);
        writer.Write(page.MaximumTick);
        writer.Write(page.MaximumActiveEndTick);
        writer.Write(page.MinimumId.Value);
        writer.Write(page.MaximumId.Value);
        writer.Write(page.MinimumKey);
        writer.Write(page.MaximumKey);
        writer.Write(page.Offset);
        writer.Write(page.StoredByteCount);
        writer.Write(page.DecodedByteCount);
        writer.Write(page.DecodedSha256);
    }

    private static void ValidateSegmentId(MidoraId value)
    {
        if (value == default) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The content pack is already complete.");
    }

    private void DisposeStreams()
    {
        if (_streamsDisposed) return;
        _streamsDisposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }

    private sealed class PageBuilder
    {
        private readonly Action<PageBuilder> _flush;
        private readonly MemoryStream _buffer = new(64 * 1024);
        private readonly BinaryWriter _writer;
        private int _recordCount;
        private long _minimumTick;
        private long _maximumTick;
        private long _maximumActiveEndTick = -1;
        private MidoraId _minimumId;
        private MidoraId _maximumId;
        private int _minimumKey;
        private int _maximumKey;
        private readonly List<DirectMidiNoteValue>? _noteEndpoints;
        private readonly List<DirectMidiChannelEventValue>? _channelEndpoints;

        public PageBuilder(
            MidoraId segmentId,
            PureMidiContentRecordKind kind,
            Action<PageBuilder> flush)
        {
            SegmentId = segmentId;
            Kind = kind;
            _flush = flush;
            _writer = new(_buffer, System.Text.Encoding.UTF8, leaveOpen: true);
            if (kind is PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint)
            {
                _noteEndpoints = [];
            }
            else if (kind == PureMidiContentRecordKind.ChannelEventEndpoint)
            {
                _channelEndpoints = [];
            }
        }

        public MidoraId SegmentId { get; }
        public PureMidiContentRecordKind Kind { get; }
        public int RecordCount => _recordCount;
        public int LastPageRecordCount { get; private set; }
        public long LastPageMinimumTick { get; private set; }
        public long LastPageMaximumTick { get; private set; }
        public long LastPageMaximumActiveEndTick { get; private set; }
        public MidoraId LastPageMinimumId { get; private set; }
        public MidoraId LastPageMaximumId { get; private set; }
        public int LastPageMinimumKey { get; private set; }
        public int LastPageMaximumKey { get; private set; }

        public void AddNote(DirectMidiNoteValue value)
        {
            if (Kind is not (PureMidiContentRecordKind.Note
                or PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint))
            {
                throw new InvalidOperationException();
            }
            const int recordBytes = 52;
            EnsureRoom(recordBytes);
            if (_noteEndpoints is null)
            {
                WriteNote(_writer, value);
                Record(
                    value.Id,
                    value.StartTick,
                    SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks)),
                    value.Key);
                return;
            }
            _noteEndpoints.Add(value);
            long noteEnd = SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks));
            long endpointTick = Kind == PureMidiContentRecordKind.NoteOnEndpoint
                ? value.StartTick
                : noteEnd;
            Record(
                value.Id,
                endpointTick,
                endpointTick,
                value.Key,
                Kind == PureMidiContentRecordKind.NoteOnEndpoint ? noteEnd : -1);
        }

        public void AddChannelEvent(DirectMidiChannelEventValue value)
        {
            if (Kind is not (PureMidiContentRecordKind.ChannelEvent
                or PureMidiContentRecordKind.ChannelEventEndpoint))
            {
                throw new InvalidOperationException();
            }
            const int recordBytes = 36;
            EnsureRoom(recordBytes);
            if (_channelEndpoints is null)
            {
                WriteChannelEvent(_writer, value);
            }
            else
            {
                _channelEndpoints.Add(value);
            }
            Record(value.Id, value.Tick, value.Tick, -1);
        }

        public void AddOpaqueEvent(OpaqueMidiEventValue value)
        {
            if (Kind != PureMidiContentRecordKind.OpaqueEvent) throw new InvalidOperationException();
            int recordBytes = checked(33 + value.Payload.Length);
            if (recordBytes > MaximumDecodedPageByteCount)
            {
                throw new InvalidDataException(
                    $"Opaque MIDI event {value.Id.Value} exceeds the decoded page byte limit.");
            }
            EnsureRoom(recordBytes);
            _writer.Write(value.Id.Value);
            _writer.Write(value.Tick);
            _writer.Write((int)value.Kind);
            _writer.Write(value.MetaType);
            _writer.Write(value.Payload.Length);
            _writer.Write(value.Payload.Span);
            _writer.Write(value.Order);
            Record(value.Id, value.Tick, value.Tick, -1);
        }

        public ArraySegment<byte> GetDecodedPage()
        {
            if (_noteEndpoints is not null)
            {
                _noteEndpoints.Sort(Kind == PureMidiContentRecordKind.NoteOnEndpoint
                    ? NoteOnEndpointComparer.Instance
                    : NoteOffEndpointComparer.Instance);
                foreach (DirectMidiNoteValue value in _noteEndpoints)
                {
                    WriteNote(_writer, value);
                }
            }
            else if (_channelEndpoints is not null)
            {
                _channelEndpoints.Sort(ChannelEndpointComparer.Instance);
                foreach (DirectMidiChannelEventValue value in _channelEndpoints)
                {
                    WriteChannelEvent(_writer, value);
                }
            }
            _writer.Flush();
            LastPageRecordCount = _recordCount;
            LastPageMinimumTick = _minimumTick;
            LastPageMaximumTick = _maximumTick;
            LastPageMaximumActiveEndTick = _maximumActiveEndTick;
            LastPageMinimumId = _minimumId;
            LastPageMaximumId = _maximumId;
            LastPageMinimumKey = _minimumKey;
            LastPageMaximumKey = _maximumKey;
            return new(_buffer.GetBuffer(), 0, checked((int)_buffer.Length));
        }

        public void ResetPage()
        {
            _buffer.SetLength(0);
            _buffer.Position = 0;
            _noteEndpoints?.Clear();
            _channelEndpoints?.Clear();
            _recordCount = 0;
            _maximumActiveEndTick = -1;
        }

        private void EnsureRoom(int nextRecordBytes)
        {
            int maximumRecords = Kind is PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint
                or PureMidiContentRecordKind.ChannelEventEndpoint
                    ? MaximumEndpointPageRecordCount
                    : MaximumPageRecordCount;
            if (_recordCount != 0
                && (_recordCount >= maximumRecords
                    || _buffer.Length + nextRecordBytes > MaximumDecodedPageByteCount))
            {
                _flush(this);
            }
        }

        private void Record(
            MidoraId id,
            long minimumTick,
            long maximumTick,
            int key,
            long maximumActiveEndTick = -1)
        {
            if (_recordCount == 0)
            {
                _minimumTick = minimumTick;
                _maximumTick = maximumTick;
                _minimumId = id;
                _maximumId = id;
                _minimumKey = key;
                _maximumKey = key;
                _maximumActiveEndTick = maximumActiveEndTick;
            }
            else
            {
                _minimumTick = Math.Min(_minimumTick, minimumTick);
                _maximumTick = Math.Max(_maximumTick, maximumTick);
                if (id.CompareTo(_minimumId) < 0) _minimumId = id;
                if (id.CompareTo(_maximumId) > 0) _maximumId = id;
                if (key >= 0)
                {
                    _minimumKey = Math.Min(_minimumKey, key);
                    _maximumKey = Math.Max(_maximumKey, key);
                }
                _maximumActiveEndTick = Math.Max(
                    _maximumActiveEndTick,
                    maximumActiveEndTick);
            }
            _recordCount++;
        }

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        private static void WriteNote(BinaryWriter writer, DirectMidiNoteValue value)
        {
            writer.Write(value.Id.Value);
            writer.Write(value.StartTick);
            writer.Write(value.LengthTicks);
            writer.Write(value.Key);
            writer.Write(value.NoteOnVelocity);
            writer.Write(value.NoteOffVelocity);
            writer.Write(value.NoteOnOrder);
            writer.Write(value.NoteOffOrder);
        }

        private static void WriteChannelEvent(
            BinaryWriter writer,
            DirectMidiChannelEventValue value)
        {
            writer.Write(value.Id.Value);
            writer.Write(value.Tick);
            writer.Write((int)value.Kind);
            writer.Write(value.Data1);
            writer.Write(value.Data2);
            writer.Write(value.Order);
        }

        private sealed class NoteOnEndpointComparer : IComparer<DirectMidiNoteValue>
        {
            public static NoteOnEndpointComparer Instance { get; } = new();

            public int Compare(DirectMidiNoteValue x, DirectMidiNoteValue y)
            {
                int result = x.StartTick.CompareTo(y.StartTick);
                if (result != 0) return result;
                result = x.NoteOnOrder.CompareTo(y.NoteOnOrder);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }

        private sealed class NoteOffEndpointComparer : IComparer<DirectMidiNoteValue>
        {
            public static NoteOffEndpointComparer Instance { get; } = new();

            public int Compare(DirectMidiNoteValue x, DirectMidiNoteValue y)
            {
                int result = SaturatingAdd(x.StartTick, Math.Max(1, x.LengthTicks))
                    .CompareTo(SaturatingAdd(y.StartTick, Math.Max(1, y.LengthTicks)));
                if (result != 0) return result;
                result = x.NoteOffOrder.CompareTo(y.NoteOffOrder);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }

        private sealed class ChannelEndpointComparer :
            IComparer<DirectMidiChannelEventValue>
        {
            public static ChannelEndpointComparer Instance { get; } = new();

            public int Compare(
                DirectMidiChannelEventValue x,
                DirectMidiChannelEventValue y)
            {
                int result = x.Tick.CompareTo(y.Tick);
                if (result != 0) return result;
                result = x.Order.CompareTo(y.Order);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }
    }

    private sealed record PageDescriptor(
        MidoraId SegmentId,
        PureMidiContentRecordKind Kind,
        int FirstOrdinal,
        int RecordCount,
        long MinimumTick,
        long MaximumTick,
        long MaximumActiveEndTick,
        MidoraId MinimumId,
        MidoraId MaximumId,
        int MinimumKey,
        int MaximumKey,
        long Offset,
        int StoredByteCount,
        int DecodedByteCount,
        byte[] DecodedSha256);
}

public sealed class PureMidiContentPackDecodedCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = [];
    private readonly LinkedList<CacheKey> _lru = [];
    private long _byteCount;
    private bool _disposed;

    public PureMidiContentPackDecodedCache(
        long byteLimit = PureMidiContentPack.DefaultDecodedCacheByteLimit)
    {
        if (byteLimit < PureMidiContentPackWriter.MaximumDecodedPageByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteLimit));
        ByteLimit = byteLimit;
    }

    public long ByteLimit { get; }

    public long ByteCount
    {
        get
        {
            lock (_gate) return _byteCount;
        }
    }

    internal object GetOrAdd(
        PureMidiContentPack owner,
        int pageIndex,
        int decodedByteCount,
        Func<object> factory,
        out bool cacheHit)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(factory);
        var key = new CacheKey(owner, pageIndex);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out CacheEntry? cached))
            {
                cacheHit = true;
                _lru.Remove(cached.Node);
                _lru.AddFirst(cached.Node);
                return cached.Value;
            }
        }

        // Decompression and checksum validation can take milliseconds. Keeping it
        // outside the global cache lock prevents an audio miss in one Track from
        // blocking an unrelated UI range query in another Track.
        object value = factory();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out CacheEntry? concurrentlyAdded))
            {
                cacheHit = true;
                _lru.Remove(concurrentlyAdded.Node);
                _lru.AddFirst(concurrentlyAdded.Node);
                return concurrentlyAdded.Value;
            }

            cacheHit = false;
            LinkedListNode<CacheKey> node = _lru.AddFirst(key);
            _entries.Add(key, new(value, decodedByteCount, node));
            _byteCount += decodedByteCount;
            while (_byteCount > ByteLimit && _lru.Last is not null)
            {
                CacheKey evictedKey = _lru.Last.Value;
                if (evictedKey == key && _entries.Count == 1) break;
                CacheEntry evicted = _entries[evictedKey];
                _lru.RemoveLast();
                _entries.Remove(evictedKey);
                _byteCount -= evicted.DecodedByteCount;
            }
            return value;
        }
    }

    internal void RemoveOwner(PureMidiContentPack owner)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (CacheKey key in _entries.Keys.Where(value => ReferenceEquals(value.Owner, owner)).ToArray())
            {
                CacheEntry entry = _entries[key];
                _entries.Remove(key);
                _lru.Remove(entry.Node);
                _byteCount -= entry.DecodedByteCount;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            _lru.Clear();
            _byteCount = 0;
        }
    }

    private readonly record struct CacheKey(PureMidiContentPack Owner, int PageIndex);

    private sealed record CacheEntry(
        object Value,
        int DecodedByteCount,
        LinkedListNode<CacheKey> Node);
}

public sealed class PureMidiContentPack : IDisposable
{
    public const long DefaultDecodedCacheByteLimit = 64L * 1024 * 1024;

    private const int HeaderByteCount = 48;
    private const int DirectoryEntryByteCount = 116;
    private const int FooterByteCount = 48;
    private const int Version = 3;
    private static ReadOnlySpan<byte> HeaderMagic => "MIDMPK3\0"u8;
    private static ReadOnlySpan<byte> FooterMagic => "MIDMPKF\0"u8;

    private readonly SafeFileHandle _handle;
    private readonly PageDescriptor[] _pages;
    private readonly Dictionary<MidoraId, SegmentSource> _sources;
    private readonly PureMidiContentPackDecodedCache _decodedCache;
    private readonly bool _ownsDecodedCache;
    private long _pageCacheHitCount;
    private long _pageCacheMissCount;
    private bool _disposed;

    private PureMidiContentPack(
        string path,
        SafeFileHandle handle,
        PageDescriptor[] pages,
        string fingerprint,
        PureMidiContentPackDecodedCache decodedCache,
        bool ownsDecodedCache)
    {
        Path = path;
        _handle = handle;
        _pages = pages;
        ContentFingerprint = fingerprint;
        _decodedCache = decodedCache;
        _ownsDecodedCache = ownsDecodedCache;
        _sources = pages
            .Select(value => value.SegmentId)
            .Distinct()
            .ToDictionary(value => value, value => new SegmentSource(this, value));
    }

    public string Path { get; }
    public string ContentFingerprint { get; }
    public int PageCount => _pages.Length;
    public long DecodedCacheByteLimit => _decodedCache.ByteLimit;
    public long DecodedCacheByteCount => _decodedCache.ByteCount;
    public long PageCacheHitCount => Interlocked.Read(ref _pageCacheHitCount);
    public long PageCacheMissCount => Interlocked.Read(ref _pageCacheMissCount);

    public static PureMidiContentPack Open(
        string path,
        long decodedCacheByteLimit = DefaultDecodedCacheByteLimit)
    {
        PureMidiContentPackDecodedCache cache = new(decodedCacheByteLimit);
        try
        {
            return OpenCore(path, cache, ownsDecodedCache: true);
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    public static PureMidiContentPack Open(
        string path,
        PureMidiContentPackDecodedCache decodedCache)
    {
        ArgumentNullException.ThrowIfNull(decodedCache);
        return OpenCore(path, decodedCache, ownsDecodedCache: false);
    }

    private static PureMidiContentPack OpenCore(
        string path,
        PureMidiContentPackDecodedCache decodedCache,
        bool ownsDecodedCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.RandomAccess);
        try
        {
            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength < HeaderByteCount + FooterByteCount)
                throw new InvalidDataException("Pure MIDI content pack is truncated.");
            byte[] header = ReadExactly(handle, 0, HeaderByteCount);
            if (!header.AsSpan(0, 8).SequenceEqual(HeaderMagic))
                throw new InvalidDataException("Pure MIDI content pack magic is invalid.");
            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            int headerBytes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            int pageCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            int reserved = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            long directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
            long directoryLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
            long footerOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
            if (version != Version || headerBytes != HeaderByteCount || reserved != 0 || pageCount < 0)
                throw new InvalidDataException("Pure MIDI content pack header is unsupported or malformed.");
            long expectedDirectoryLength = checked((long)pageCount * DirectoryEntryByteCount);
            if (directoryLength != expectedDirectoryLength
                || directoryOffset < HeaderByteCount
                || footerOffset != checked(directoryOffset + directoryLength)
                || checked(footerOffset + FooterByteCount) != fileLength
                || directoryLength > int.MaxValue)
            {
                throw new InvalidDataException("Pure MIDI content pack offsets are inconsistent.");
            }
            byte[] directory = ReadExactly(handle, directoryOffset, checked((int)directoryLength));
            byte[] footer = ReadExactly(handle, footerOffset, FooterByteCount);
            if (!footer.AsSpan(0, 8).SequenceEqual(FooterMagic))
                throw new InvalidDataException("Pure MIDI content pack footer magic is invalid.");
            byte[] actualDirectoryHash = SHA256.HashData(directory);
            if (!footer.AsSpan(8, 32).SequenceEqual(actualDirectoryHash))
                throw new InvalidDataException("Pure MIDI content pack directory checksum is invalid.");
            if (BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(40)) != fileLength)
                throw new InvalidDataException("Pure MIDI content pack footer length is invalid.");

            PageDescriptor[] pages = ParseDirectory(directory, pageCount, directoryOffset);
            return new(
                fullPath,
                handle,
                pages,
                Convert.ToHexStringLower(actualDirectoryHash),
                decodedCache,
                ownsDecodedCache);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IPureMidiSegmentContentSource GetSegmentSource(MidoraId segmentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sources.TryGetValue(segmentId, out SegmentSource? source)
            ? source
            : new SegmentSource(this, segmentId);
    }

    public IReadOnlyList<MidoraId> SegmentIds => _sources.Keys.Order().ToArray();

    public void CopyTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using FileStream source = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        source.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decodedCache.RemoveOwner(this);
        _handle.Dispose();
        if (_ownsDecodedCache) _decodedCache.Dispose();
    }

    private object GetDecodedPage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PageDescriptor descriptor = _pages[pageIndex];
        object result = _decodedCache.GetOrAdd(
            this,
            pageIndex,
            descriptor.DecodedByteCount,
            () => DecodeStoredPage(pageIndex, descriptor),
            out bool cacheHit);
        if (cacheHit) Interlocked.Increment(ref _pageCacheHitCount);
        else Interlocked.Increment(ref _pageCacheMissCount);
        return result;
    }

    private object DecodeStoredPage(int pageIndex, PageDescriptor descriptor)
    {
        byte[] stored = ReadExactly(_handle, descriptor.Offset, descriptor.StoredByteCount);
        byte[] decoded = new byte[descriptor.DecodedByteCount];
        try
        {
            using MemoryStream compressed = new(stored, writable: false);
            using BrotliStream brotli = new(compressed, CompressionMode.Decompress);
            int position = 0;
            while (position < decoded.Length)
            {
                int read = brotli.Read(decoded, position, decoded.Length - position);
                if (read == 0) break;
                position += read;
            }
            if (position != decoded.Length || brotli.ReadByte() != -1)
                throw new InvalidDataException($"Pure MIDI content page {pageIndex} decoded length is invalid.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Pure MIDI content page {pageIndex} compressed payload is invalid.",
                exception);
        }
        if (!SHA256.HashData(decoded).AsSpan().SequenceEqual(descriptor.DecodedSha256))
            throw new InvalidDataException($"Pure MIDI content page {pageIndex} checksum is invalid.");
        return DecodePage(descriptor, decoded);
    }

    private static object DecodePage(PageDescriptor descriptor, byte[] decoded)
    {
        using MemoryStream stream = new(decoded, writable: false);
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        object result;
        switch (descriptor.Kind)
        {
            case PureMidiContentRecordKind.Note:
            case PureMidiContentRecordKind.NoteOnEndpoint:
            case PureMidiContentRecordKind.NoteOffEndpoint:
                {
                    DirectMidiNoteValue[] values = new DirectMidiNoteValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        values[index] = new(
                            MidoraId.FromSequence(reader.ReadInt64()),
                            reader.ReadInt64(),
                            reader.ReadInt64(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt64(),
                            reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            case PureMidiContentRecordKind.ChannelEvent:
            case PureMidiContentRecordKind.ChannelEventEndpoint:
                {
                    DirectMidiChannelEventValue[] values = new DirectMidiChannelEventValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        values[index] = new(
                            MidoraId.FromSequence(reader.ReadInt64()),
                            reader.ReadInt64(),
                            (DirectMidiChannelEventKind)reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            case PureMidiContentRecordKind.OpaqueEvent:
                {
                    OpaqueMidiEventValue[] values = new OpaqueMidiEventValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        MidoraId id = MidoraId.FromSequence(reader.ReadInt64());
                        long tick = reader.ReadInt64();
                        OpaqueMidiEventKind kind = (OpaqueMidiEventKind)reader.ReadInt32();
                        byte metaType = reader.ReadByte();
                        int payloadLength = reader.ReadInt32();
                        if (payloadLength < 0 || payloadLength > decoded.Length - stream.Position - 8)
                            throw new InvalidDataException("Pure MIDI opaque payload length is invalid.");
                        byte[] payload = reader.ReadBytes(payloadLength);
                        if (payload.Length != payloadLength)
                            throw new InvalidDataException("Pure MIDI opaque payload is truncated.");
                        values[index] = new(id, tick, kind, metaType, payload, reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported Pure MIDI content page kind {descriptor.Kind}.");
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Pure MIDI content page has trailing bytes.");
        ValidateDecodedPage(descriptor, result);
        return result;
    }

    private static void ValidateDecodedPage(PageDescriptor descriptor, object decoded)
    {
        long minimumTick = long.MaxValue;
        long maximumTick = long.MinValue;
        long maximumActiveEndTick = -1;
        MidoraId minimumId = new(long.MaxValue);
        MidoraId maximumId = default;
        int minimumKey = int.MaxValue;
        int maximumKey = int.MinValue;
        HashSet<MidoraId> ids = new(descriptor.RecordCount);

        switch (decoded)
        {
            case DirectMidiNoteValue[] notes:
                foreach (DirectMidiNoteValue value in notes)
                {
                    if (value.StartTick < 0 || value.LengthTicks <= 0
                        || value.StartTick > long.MaxValue - value.LengthTicks
                        || value.Key is < 0 or > 127
                        || value.NoteOnVelocity is < 1 or > 127
                        || value.NoteOffVelocity is < 0 or > 127
                        || value.NoteOnOrder < 0 || value.NoteOffOrder < 0)
                    {
                        throw new InvalidDataException("Pure MIDI Note page contains an invalid record.");
                    }
                    long endTick = value.StartTick + value.LengthTicks;
                    if (descriptor.Kind == PureMidiContentRecordKind.NoteOnEndpoint)
                    {
                        Record(value.Id, value.StartTick, value.StartTick, value.Key);
                        maximumActiveEndTick = Math.Max(maximumActiveEndTick, endTick);
                    }
                    else if (descriptor.Kind == PureMidiContentRecordKind.NoteOffEndpoint)
                    {
                        Record(value.Id, endTick, endTick, value.Key);
                    }
                    else
                    {
                        Record(value.Id, value.StartTick, endTick, value.Key);
                    }
                }
                break;
            case DirectMidiChannelEventValue[] events:
                foreach (DirectMidiChannelEventValue value in events)
                {
                    bool oneByte = value.Kind is DirectMidiChannelEventKind.ProgramChange
                        or DirectMidiChannelEventKind.ChannelPressure;
                    if (value.Tick < 0 || !Enum.IsDefined(value.Kind)
                        || value.Data1 is < 0 or > 127
                        || value.Data2 < 0 || value.Data2 > (oneByte ? 0 : 127)
                        || value.Order < 0)
                    {
                        throw new InvalidDataException("Pure MIDI channel-event page contains an invalid record.");
                    }
                    Record(value.Id, value.Tick, value.Tick, -1);
                }
                break;
            case OpaqueMidiEventValue[] opaque:
                foreach (OpaqueMidiEventValue value in opaque)
                {
                    if (value.Tick < 0 || !Enum.IsDefined(value.Kind) || value.Order < 0)
                        throw new InvalidDataException("Pure MIDI opaque-event page contains an invalid record.");
                    Record(value.Id, value.Tick, value.Tick, -1);
                }
                break;
            default:
                throw new InvalidDataException("Pure MIDI content page decoded to an unexpected record type.");
        }

        if (ids.Count != descriptor.RecordCount
            || minimumTick != descriptor.MinimumTick
            || maximumTick != descriptor.MaximumTick
            || maximumActiveEndTick != descriptor.MaximumActiveEndTick
            || minimumId != descriptor.MinimumId
            || maximumId != descriptor.MaximumId
            || IsNoteKind(descriptor.Kind)
                && (minimumKey != descriptor.MinimumKey || maximumKey != descriptor.MaximumKey)
            || !IsNoteKind(descriptor.Kind)
                && (descriptor.MinimumKey != -1 || descriptor.MaximumKey != -1))
        {
            throw new InvalidDataException("Pure MIDI content page metadata does not match its decoded records.");
        }

        void Record(MidoraId id, long startTick, long endTick, int key)
        {
            if (!ids.Add(id))
                throw new InvalidDataException("Pure MIDI content page contains a duplicate stable ID.");
            minimumTick = Math.Min(minimumTick, startTick);
            maximumTick = Math.Max(maximumTick, endTick);
            if (id.CompareTo(minimumId) < 0) minimumId = id;
            if (id.CompareTo(maximumId) > 0) maximumId = id;
            if (key >= 0)
            {
                minimumKey = Math.Min(minimumKey, key);
                maximumKey = Math.Max(maximumKey, key);
            }
        }

        static bool IsNoteKind(PureMidiContentRecordKind kind) =>
            kind is PureMidiContentRecordKind.Note
                or PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint;
    }

    private static PageDescriptor[] ParseDirectory(byte[] bytes, int pageCount, long directoryOffset)
    {
        PageDescriptor[] pages = new PageDescriptor[pageCount];
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);
        Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), int> nextOrdinals = [];
        long previousPayloadEnd = HeaderByteCount;
        for (int index = 0; index < pages.Length; index++)
        {
            PureMidiContentRecordKind kind = (PureMidiContentRecordKind)reader.ReadByte();
            if (reader.ReadByte() != 0 || reader.ReadByte() != 0 || reader.ReadByte() != 0)
                throw new InvalidDataException("Pure MIDI content directory reserved bytes are non-zero.");
            MidoraId segmentId = MidoraId.FromSequence(reader.ReadInt64());
            int firstOrdinal = reader.ReadInt32();
            int recordCount = reader.ReadInt32();
            long minimumTick = reader.ReadInt64();
            long maximumTick = reader.ReadInt64();
            long maximumActiveEndTick = reader.ReadInt64();
            MidoraId minimumId = MidoraId.FromSequence(reader.ReadInt64());
            MidoraId maximumId = MidoraId.FromSequence(reader.ReadInt64());
            int minimumKey = reader.ReadInt32();
            int maximumKey = reader.ReadInt32();
            long offset = reader.ReadInt64();
            int storedBytes = reader.ReadInt32();
            int decodedBytes = reader.ReadInt32();
            byte[] checksum = reader.ReadBytes(32);
            int maximumRecords = kind is PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint
                or PureMidiContentRecordKind.ChannelEventEndpoint
                    ? PureMidiContentPackWriter.MaximumEndpointPageRecordCount
                    : PureMidiContentPackWriter.MaximumPageRecordCount;
            if (!Enum.IsDefined(kind)
                || recordCount <= 0
                || recordCount > maximumRecords
                || firstOrdinal < 0
                || minimumTick > maximumTick
                || kind == PureMidiContentRecordKind.NoteOnEndpoint
                    && maximumActiveEndTick <= maximumTick
                || kind != PureMidiContentRecordKind.NoteOnEndpoint
                    && maximumActiveEndTick != -1
                || minimumId.CompareTo(maximumId) > 0
                || offset < HeaderByteCount
                || storedBytes <= 0
                || storedBytes > PureMidiContentPackWriter.MaximumDecodedPageByteCount + 65_536
                || decodedBytes is <= 0 or > PureMidiContentPackWriter.MaximumDecodedPageByteCount
                || offset > directoryOffset - storedBytes
                || checksum.Length != 32)
            {
                throw new InvalidDataException($"Pure MIDI content directory entry {index} is invalid.");
            }
            var key = (segmentId, kind);
            int expectedOrdinal = nextOrdinals.GetValueOrDefault(key);
            if (firstOrdinal != expectedOrdinal)
                throw new InvalidDataException($"Pure MIDI content page ordinal {index} is discontinuous.");
            nextOrdinals[key] = checked(firstOrdinal + recordCount);
            long payloadEnd = checked(offset + storedBytes);
            if (offset < previousPayloadEnd)
                throw new InvalidDataException($"Pure MIDI content page {index} overlaps or precedes another payload extent.");
            previousPayloadEnd = payloadEnd;
            pages[index] = new(
                index,
                segmentId,
                kind,
                firstOrdinal,
                recordCount,
                minimumTick,
                maximumTick,
                maximumActiveEndTick,
                minimumId,
                maximumId,
                minimumKey,
                maximumKey,
                offset,
                storedBytes,
                decodedBytes,
                checksum);
        }
        return pages;
    }

    private static byte[] ReadExactly(SafeFileHandle handle, long offset, int byteCount)
    {
        byte[] result = new byte[byteCount];
        int position = 0;
        while (position < result.Length)
        {
            int read = RandomAccess.Read(handle, result.AsSpan(position), offset + position);
            if (read == 0) throw new EndOfStreamException("Pure MIDI content pack is truncated.");
            position += read;
        }
        return result;
    }

    private sealed class SegmentSource :
        IPureMidiSegmentContentSource,
        IPureMidiPlaybackEndpointSource,
        IPureMidiContentOverviewSource,
        IPureMidiContentPackSegmentSource
    {
        private readonly PureMidiContentPack _owner;
        private readonly PageDescriptor[] _notePages;
        private readonly PageDescriptor[] _noteOnEndpointPages;
        private readonly PageDescriptor[] _noteOffEndpointPages;
        private readonly PageDescriptor[] _channelPages;
        private readonly PageDescriptor[] _channelEndpointPages;
        private readonly PageDescriptor[] _opaquePages;

        public SegmentSource(PureMidiContentPack owner, MidoraId segmentId)
        {
            _owner = owner;
            SegmentId = segmentId;
            _notePages = Pages(PureMidiContentRecordKind.Note);
            _noteOnEndpointPages = Pages(PureMidiContentRecordKind.NoteOnEndpoint);
            _noteOffEndpointPages = Pages(PureMidiContentRecordKind.NoteOffEndpoint);
            _channelPages = Pages(PureMidiContentRecordKind.ChannelEvent);
            _channelEndpointPages = Pages(PureMidiContentRecordKind.ChannelEventEndpoint);
            _opaquePages = Pages(PureMidiContentRecordKind.OpaqueEvent);

            if (Count(_notePages) != Count(_noteOnEndpointPages)
                || Count(_notePages) != Count(_noteOffEndpointPages)
                || Count(_channelPages) != Count(_channelEndpointPages))
            {
                throw new InvalidDataException(
                    $"Pure MIDI content indexes for Segment {segmentId.Value} do not match their source record counts.");
            }

            PageDescriptor[] Pages(PureMidiContentRecordKind kind) => owner._pages
                .Where(value => value.SegmentId == segmentId && value.Kind == kind)
                .OrderBy(value => value.FirstOrdinal)
                .ToArray();
        }

        public MidoraId SegmentId { get; }
        PureMidiContentPack IPureMidiContentPackSegmentSource.Owner => _owner;
        public int NoteCount => Count(_notePages);
        public int ChannelEventCount => Count(_channelPages);
        public int OpaqueEventCount => Count(_opaquePages);
        public string ContentFingerprint => _owner.ContentFingerprint + ":" + SegmentId.Value;

        public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() =>
            _noteOnEndpointPages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount));

        public IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries() =>
            _channelEndpointPages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount));

        public IEnumerable<PureMidiContentRangeSummary> GetOpaqueEventRangeSummaries() =>
            _opaquePages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount));

        public bool TryAccumulateNoteStartColumns(
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(extent, destination);
            if (destination.IsEmpty) return true;
            AccumulateNoteStartPages(
                _noteOnEndpointPages,
                extent,
                destination,
                excludedIds);
            return true;
        }

        public bool TryAccumulateChannelEventColumns(
            long extent,
            Span<byte> noteStartColumns,
            Span<byte> eventColumns,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(
                extent,
                noteStartColumns,
                eventColumns);
            if (noteStartColumns.IsEmpty) return true;
            foreach (PageDescriptor page in _channelEndpointPages)
            {
                foreach (DirectMidiChannelEventValue value in
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        value.Kind,
                        value.Data2,
                        value.Tick,
                        extent,
                        noteStartColumns,
                        eventColumns);
                }
            }
            return true;
        }

        public bool TryAccumulateOpaqueEventColumns(
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(extent, destination);
            if (destination.IsEmpty) return true;
            foreach (PageDescriptor page in _opaquePages)
            {
                int firstColumn = PureMidiOverviewProjection.Column(
                    page.MinimumTick,
                    extent,
                    destination.Length);
                int lastColumn = PureMidiOverviewProjection.Column(
                    page.MaximumTick,
                    extent,
                    destination.Length);
                if (firstColumn == lastColumn
                    && !MayContainExcludedId(page, excludedIds))
                {
                    destination[firstColumn] = 1;
                    continue;
                }

                foreach (OpaqueMidiEventValue value in
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        destination,
                        value.Tick,
                        extent);
                }
            }
            return true;
        }

        public DirectMidiNoteValue GetNote(int index)
        {
            PageDescriptor page = FindPage(_notePages, index);
            return ((DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public DirectMidiChannelEventValue GetChannelEvent(int index)
        {
            PageDescriptor page = FindPage(_channelPages, index);
            return ((DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            PageDescriptor page = FindPage(_opaquePages, index);
            return ((OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public int FindNoteIndex(MidoraId id) => FindById(_notePages, id, static (page, owner) =>
            ((DirectMidiNoteValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public int FindChannelEventIndex(MidoraId id) => FindById(_channelPages, id, static (page, owner) =>
            ((DirectMidiChannelEventValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public int FindOpaqueEventIndex(MidoraId id) => FindById(_opaquePages, id, static (page, owner) =>
            ((OpaqueMidiEventValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _notePages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiNoteValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesAtStarts(
            IReadOnlySet<DirectMidiNoteStartKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (keys.Count == 0) yield break;
            Dictionary<long, HashSet<int>> keysByTick = keys
                .GroupBy(static key => key.Tick)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static key => key.Key).ToHashSet());
            long[] sortedTicks = keysByTick.Keys.Order().ToArray();
            foreach (PageDescriptor page in _noteOnEndpointPages)
            {
                if (!MayContainRequestedTick(page, sortedTicks)) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int tickIndex = Array.BinarySearch(sortedTicks, page.MinimumTick);
                if (tickIndex < 0) tickIndex = ~tickIndex;
                while (tickIndex < sortedTicks.Length
                    && sortedTicks[tickIndex] <= page.MaximumTick)
                {
                    long tick = sortedTicks[tickIndex++];
                    HashSet<int> requestedKeys = keysByTick[tick];
                    int index = LowerBoundNote(values, tick, noteOn: true);
                    while (index < values.Length && values[index].StartTick == tick)
                    {
                        DirectMidiNoteValue value = values[index++];
                        if (requestedKeys.Contains(value.Key))
                        {
                            // Endpoint pages are ordered by tick rather than source
                            // ordinal. Note collision resolution never removes an
                            // untouched source incumbent, so the source index is not
                            // needed for this bounded lookup.
                            yield return new(-1, value);
                        }
                    }
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _channelPages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiChannelEventValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsAtStarts(
            IReadOnlySet<DirectMidiEventStartKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (keys.Count == 0) yield break;
            long[] sortedTicks = keys.Select(static key => key.Tick).Distinct().Order().ToArray();
            foreach (PageDescriptor page in _channelPages)
            {
                if (!MayContainRequestedTick(page, sortedTicks)) continue;
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiChannelEventValue value = values[index];
                    int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                        or DirectMidiChannelEventKind.PolyphonicKeyPressure
                        or DirectMidiChannelEventKind.NoteOn
                        or DirectMidiChannelEventKind.NoteOff
                            ? value.Data1
                            : 0;
                    if (keys.Contains(new(value.Tick, value.Kind, selector)))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _opaquePages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                OpaqueMidiEventValue[] values =
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    OpaqueMidiEventValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127)
        {
            foreach (PageDescriptor page in _notePages)
            {
                if (page.MinimumTick >= endTick || page.MaximumTick <= startTick
                    || page.MinimumKey > maximumKey || page.MaximumKey < minimumKey)
                    continue;
                foreach (DirectMidiNoteValue value in (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))
                {
                    long noteEnd = value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
                        ? long.MaxValue
                        : value.StartTick + Math.Max(1, value.LengthTicks);
                    if (value.StartTick < endTick && noteEnd > startTick
                        && value.Key >= minimumKey && value.Key <= maximumKey)
                        yield return value;
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick)
        {
            foreach (PageDescriptor page in _channelPages)
            {
                if (page.MinimumTick >= endTick || page.MaximumTick < startTick) continue;
                foreach (DirectMidiChannelEventValue value in
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))
                    if (value.Tick >= startTick && value.Tick < endTick) yield return value;
            }
        }

        public IEnumerable<DirectMidiNoteValue> QueryNoteStarts(
            long startTick,
            long endTick) =>
            QueryNoteEndpoints(
                _noteOnEndpointPages,
                startTick,
                endTick,
                noteOn: true);

        public IEnumerable<DirectMidiNoteValue> QueryNoteEnds(
            long startTick,
            long endTick) =>
            QueryNoteEndpoints(
                _noteOffEndpointPages,
                startTick,
                endTick,
                noteOn: false);

        public IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick)
        {
            if (tick <= 0) yield break;
            foreach (PageDescriptor page in _noteOnEndpointPages)
            {
                if (page.MinimumTick >= tick || page.MaximumActiveEndTick <= tick) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int exclusiveEnd = LowerBoundNote(values, tick, noteOn: true);
                for (int index = 0; index < exclusiveEnd; index++)
                {
                    DirectMidiNoteValue value = values[index];
                    if (SaturatingAdd(value.StartTick, value.LengthTicks) > tick)
                    {
                        yield return value;
                    }
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(
            long startTick,
            long endTick)
        {
            if (endTick <= startTick) yield break;
            PriorityQueue<ChannelEndpointCursor, ChannelEndpointPriority> queue = new();
            foreach (PageDescriptor page in _channelEndpointPages)
            {
                if (page.MinimumTick >= endTick || page.MaximumTick < startTick) continue;
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                int index = LowerBoundChannel(values, startTick);
                if (index < values.Length && values[index].Tick < endTick)
                {
                    ChannelEndpointCursor cursor = new(values, index);
                    queue.Enqueue(cursor, ChannelPriority(cursor.Current));
                }
            }
            while (queue.TryDequeue(
                out ChannelEndpointCursor? cursor,
                out _))
            {
                DirectMidiChannelEventValue value = cursor.Current;
                yield return value;
                if (cursor.MoveNext() && cursor.Current.Tick < endTick)
                {
                    queue.Enqueue(cursor, ChannelPriority(cursor.Current));
                }
            }
        }

        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick)
        {
            foreach (PageDescriptor page in _opaquePages)
            {
                if (page.MinimumTick >= endTick || page.MaximumTick < startTick) continue;
                foreach (OpaqueMidiEventValue value in
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))
                    if (value.Tick >= startTick && value.Tick < endTick) yield return value;
            }
        }

        private IEnumerable<DirectMidiNoteValue> QueryNoteEndpoints(
            PageDescriptor[] pages,
            long startTick,
            long endTick,
            bool noteOn)
        {
            if (endTick <= startTick) yield break;
            PriorityQueue<NoteEndpointCursor, NoteEndpointPriority> queue = new();
            foreach (PageDescriptor page in pages)
            {
                if (page.MinimumTick >= endTick || page.MaximumTick < startTick) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int index = LowerBoundNote(values, startTick, noteOn);
                if (index < values.Length && EndpointTick(values[index], noteOn) < endTick)
                {
                    NoteEndpointCursor cursor = new(values, index);
                    queue.Enqueue(cursor, NotePriority(cursor.Current, noteOn));
                }
            }
            while (queue.TryDequeue(out NoteEndpointCursor? cursor, out _))
            {
                DirectMidiNoteValue value = cursor.Current;
                yield return value;
                if (cursor.MoveNext()
                    && EndpointTick(cursor.Current, noteOn) < endTick)
                {
                    queue.Enqueue(cursor, NotePriority(cursor.Current, noteOn));
                }
            }
        }

        private void AccumulateNoteStartPages(
            IEnumerable<PageDescriptor> pages,
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            foreach (PageDescriptor page in pages)
            {
                int firstColumn = PureMidiOverviewProjection.Column(
                    page.MinimumTick,
                    extent,
                    destination.Length);
                int lastColumn = PureMidiOverviewProjection.Column(
                    page.MaximumTick,
                    extent,
                    destination.Length);
                if (firstColumn == lastColumn
                    && !MayContainExcludedId(page, excludedIds))
                {
                    destination[firstColumn] = 1;
                    continue;
                }

                foreach (DirectMidiNoteValue value in
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        destination,
                        value.StartTick,
                        extent);
                }
            }
        }

        private static bool MayContainExcludedId(
            PageDescriptor page,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            if (excludedIds is null || excludedIds.Count == 0) return false;
            foreach (MidoraId id in excludedIds)
            {
                if (id.CompareTo(page.MinimumId) >= 0
                    && id.CompareTo(page.MaximumId) <= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MayContainRequestedId(
            PageDescriptor page,
            MidoraId[] sortedIds)
        {
            int low = 0;
            int high = sortedIds.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (sortedIds[middle].CompareTo(page.MinimumId) < 0) low = middle + 1;
                else high = middle;
            }
            return low < sortedIds.Length
                && sortedIds[low].CompareTo(page.MaximumId) <= 0;
        }

        private static bool MayContainRequestedTick(
            PageDescriptor page,
            long[] sortedTicks)
        {
            int low = Array.BinarySearch(sortedTicks, page.MinimumTick);
            if (low < 0) low = ~low;
            return low < sortedTicks.Length && sortedTicks[low] <= page.MaximumTick;
        }

        private static int LowerBoundNote(
            DirectMidiNoteValue[] values,
            long tick,
            bool noteOn)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (EndpointTick(values[middle], noteOn) < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static int LowerBoundChannel(
            DirectMidiChannelEventValue[] values,
            long tick)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (values[middle].Tick < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static long EndpointTick(DirectMidiNoteValue value, bool noteOn) =>
            noteOn ? value.StartTick : SaturatingAdd(value.StartTick, value.LengthTicks);

        private static long SaturatingAdd(long left, long right) =>
            right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

        private static NoteEndpointPriority NotePriority(
            DirectMidiNoteValue value,
            bool noteOn) => new(
                EndpointTick(value, noteOn),
                noteOn ? value.NoteOnOrder : value.NoteOffOrder,
                value.Id.Value);

        private static ChannelEndpointPriority ChannelPriority(
            DirectMidiChannelEventValue value) =>
            new(value.Tick, value.Order, value.Id.Value);

        private int FindById(
            IReadOnlyList<PageDescriptor> pages,
            MidoraId id,
            Func<PageDescriptor, PureMidiContentPack, IEnumerable<MidoraId>> getIds)
        {
            foreach (PageDescriptor page in pages)
            {
                if (id.CompareTo(page.MinimumId) < 0 || id.CompareTo(page.MaximumId) > 0) continue;
                int localIndex = 0;
                foreach (MidoraId value in getIds(page, _owner))
                {
                    if (value == id) return checked(page.FirstOrdinal + localIndex);
                    localIndex++;
                }
            }
            return -1;
        }

        private static int Count(IReadOnlyList<PageDescriptor> pages) => pages.Count == 0
            ? 0
            : checked(pages[^1].FirstOrdinal + pages[^1].RecordCount);

        private static PageDescriptor FindPage(IReadOnlyList<PageDescriptor> pages, int index)
        {
            if (index < 0 || pages.Count == 0 || index >= Count(pages))
                throw new ArgumentOutOfRangeException(nameof(index));
            int low = 0;
            int high = pages.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                PageDescriptor page = pages[middle];
                if (index < page.FirstOrdinal) high = middle - 1;
                else if (index >= page.FirstOrdinal + page.RecordCount) low = middle + 1;
                else return page;
            }
            throw new InvalidDataException("Pure MIDI content ordinal is missing from the page catalog.");
        }

        private sealed class NoteEndpointCursor(
            DirectMidiNoteValue[] values,
            int index)
        {
            private int _index = index;
            public DirectMidiNoteValue Current => values[_index];
            public bool MoveNext()
            {
                _index++;
                return _index < values.Length;
            }
        }

        private sealed class ChannelEndpointCursor(
            DirectMidiChannelEventValue[] values,
            int index)
        {
            private int _index = index;
            public DirectMidiChannelEventValue Current => values[_index];
            public bool MoveNext()
            {
                _index++;
                return _index < values.Length;
            }
        }

        private readonly record struct NoteEndpointPriority(
            long Tick,
            long Order,
            long StableId) : IComparable<NoteEndpointPriority>
        {
            public int CompareTo(NoteEndpointPriority other)
            {
                int result = Tick.CompareTo(other.Tick);
                if (result != 0) return result;
                result = Order.CompareTo(other.Order);
                return result != 0 ? result : StableId.CompareTo(other.StableId);
            }
        }

        private readonly record struct ChannelEndpointPriority(
            long Tick,
            long Order,
            long StableId) : IComparable<ChannelEndpointPriority>
        {
            public int CompareTo(ChannelEndpointPriority other)
            {
                int result = Tick.CompareTo(other.Tick);
                if (result != 0) return result;
                result = Order.CompareTo(other.Order);
                return result != 0 ? result : StableId.CompareTo(other.StableId);
            }
        }
    }

    private sealed record PageDescriptor(
        int Index,
        MidoraId SegmentId,
        PureMidiContentRecordKind Kind,
        int FirstOrdinal,
        int RecordCount,
        long MinimumTick,
        long MaximumTick,
        long MaximumActiveEndTick,
        MidoraId MinimumId,
        MidoraId MaximumId,
        int MinimumKey,
        int MaximumKey,
        long Offset,
        int StoredByteCount,
        int DecodedByteCount,
        byte[] DecodedSha256);

}
