using System.Collections.ObjectModel;
using System.Text;

namespace Midora.Audio;

public enum ReusableAudioReadExtentKind
{
    PackBlock = 1,
    RawPayload = 2
}

public readonly record struct ReusableAudioReadExtent(
    ReusableAudioReadExtentKind Kind,
    string Path,
    long LogicalOffset,
    long FileOffset,
    long PayloadLength,
    int BlockIndex,
    int BlockCount);

public sealed record ReusableAudioReadEntry(
    string Key,
    long PayloadLength,
    IReadOnlyList<ReusableAudioReadExtent> Extents);

public sealed class ReusableAudioReadLease : IDisposable
{
    private Action? _release;

    internal ReusableAudioReadLease(
        IReadOnlyDictionary<string, ReusableAudioReadEntry> entries,
        Action release)
    {
        Entries = entries;
        _release = release;
    }

    public IReadOnlyDictionary<string, ReusableAudioReadEntry> Entries { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        GC.SuppressFinalize(this);
    }
}

public static class ReusableAudioReadManifest
{
    private const uint Magic = 0x4D52414D; // MARM, little-endian.
    private const int Version = 1;
    private const int MaximumEntryCount = 1_000_000;
    private const int MaximumExtentCount = 16_000_000;
    private const int MaximumPathByteCount = 32_767;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static void Write(
        string path,
        IEnumerable<ReusableAudioReadEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        ReusableAudioReadEntry[] ordered = entries
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length > MaximumEntryCount)
        {
            throw new InvalidDataException("The reusable-audio read manifest has too many entries.");
        }

        using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.WriteThrough);
        using BinaryWriter writer = new(stream, StrictUtf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(ordered.Length);
        writer.Write(0);
        foreach (ReusableAudioReadEntry entry in ordered)
        {
            ValidateEntry(entry);
            writer.Write(Convert.FromHexString(entry.Key));
            writer.Write(entry.PayloadLength);
            writer.Write(entry.Extents.Count);
            writer.Write(0);
            foreach (ReusableAudioReadExtent extent in entry.Extents)
            {
                byte[] pathBytes = StrictUtf8.GetBytes(Path.GetFullPath(extent.Path));
                if (pathBytes.Length == 0 || pathBytes.Length > MaximumPathByteCount)
                {
                    throw new InvalidDataException(
                        "A reusable-audio read extent path is outside the bounded protocol.");
                }
                writer.Write((int)extent.Kind);
                writer.Write(pathBytes.Length);
                writer.Write(extent.LogicalOffset);
                writer.Write(extent.FileOffset);
                writer.Write(extent.PayloadLength);
                writer.Write(extent.BlockIndex);
                writer.Write(extent.BlockCount);
                writer.Write(0);
                writer.Write(pathBytes);
            }
        }
        stream.Flush(flushToDisk: true);
    }

    public static IReadOnlyDictionary<string, ReusableAudioReadEntry> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using BinaryReader reader = new(stream, StrictUtf8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic
            || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("The reusable-audio read manifest header is invalid.");
        }
        int entryCount = reader.ReadInt32();
        if (entryCount < 0 || entryCount > MaximumEntryCount || reader.ReadInt32() != 0)
        {
            throw new InvalidDataException("The reusable-audio read manifest entry count is invalid.");
        }
        Dictionary<string, ReusableAudioReadEntry> result = new(
            entryCount,
            StringComparer.Ordinal);
        int totalExtentCount = 0;
        for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            string key = Convert.ToHexStringLower(reader.ReadBytes(32));
            long payloadLength = reader.ReadInt64();
            int extentCount = reader.ReadInt32();
            if (payloadLength < 0
                || extentCount <= 0
                || extentCount > MaximumExtentCount - totalExtentCount
                || reader.ReadInt32() != 0)
            {
                throw new InvalidDataException("A reusable-audio read manifest entry is invalid.");
            }
            totalExtentCount += extentCount;
            ReusableAudioReadExtent[] extents = new ReusableAudioReadExtent[extentCount];
            for (int extentIndex = 0; extentIndex < extentCount; extentIndex++)
            {
                ReusableAudioReadExtentKind kind =
                    (ReusableAudioReadExtentKind)reader.ReadInt32();
                int pathLength = reader.ReadInt32();
                long logicalOffset = reader.ReadInt64();
                long fileOffset = reader.ReadInt64();
                long extentLength = reader.ReadInt64();
                int blockIndex = reader.ReadInt32();
                int blockCount = reader.ReadInt32();
                if (reader.ReadInt32() != 0
                    || pathLength <= 0
                    || pathLength > MaximumPathByteCount)
                {
                    throw new InvalidDataException(
                        "A reusable-audio read manifest extent is invalid.");
                }
                byte[] pathBytes = reader.ReadBytes(pathLength);
                if (pathBytes.Length != pathLength)
                {
                    throw new EndOfStreamException(
                        "The reusable-audio read manifest extent path is truncated.");
                }
                string extentPath = StrictUtf8.GetString(pathBytes);
                extents[extentIndex] = new(
                    kind,
                    extentPath,
                    logicalOffset,
                    fileOffset,
                    extentLength,
                    blockIndex,
                    blockCount);
            }
            ReusableAudioReadEntry entry = new(key, payloadLength, extents);
            ValidateEntry(entry);
            if (!result.TryAdd(key, entry))
            {
                throw new InvalidDataException(
                    "The reusable-audio read manifest contains a duplicate key.");
            }
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "The reusable-audio read manifest contains trailing bytes.");
        }
        return new ReadOnlyDictionary<string, ReusableAudioReadEntry>(result);
    }

    private static void ValidateEntry(ReusableAudioReadEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Key.Length != 64
            || entry.Key.Any(value => value is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            || entry.PayloadLength < 0
            || entry.Extents.Count == 0)
        {
            throw new InvalidDataException("A reusable-audio read manifest entry is invalid.");
        }
        long expectedOffset = 0;
        int? expectedBlockCount = null;
        for (int index = 0; index < entry.Extents.Count; index++)
        {
            ReusableAudioReadExtent extent = entry.Extents[index];
            if (!Enum.IsDefined(extent.Kind)
                || !Path.IsPathFullyQualified(extent.Path)
                || extent.LogicalOffset != expectedOffset
                || extent.FileOffset < 0
                || extent.PayloadLength <= 0)
            {
                throw new InvalidDataException(
                    "A reusable-audio read manifest extent schedule is invalid.");
            }
            if (extent.Kind == ReusableAudioReadExtentKind.PackBlock)
            {
                expectedBlockCount ??= extent.BlockCount;
                if (extent.BlockCount != expectedBlockCount
                    || extent.BlockCount != entry.Extents.Count
                    || extent.BlockIndex != index)
                {
                    throw new InvalidDataException(
                        "A reusable-audio Pack extent schedule is invalid.");
                }
            }
            else if (extent.BlockIndex != 0 || extent.BlockCount != 1)
            {
                throw new InvalidDataException(
                    "A raw reusable-audio extent schedule is invalid.");
            }
            expectedOffset = checked(expectedOffset + extent.PayloadLength);
        }
        if (expectedOffset != entry.PayloadLength)
        {
            throw new InvalidDataException(
                "A reusable-audio read manifest payload length is invalid.");
        }
    }
}
