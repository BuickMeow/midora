using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Midora.Audio;

/// <summary>
/// Writes reusable PCM cache records directly in the Pack wire format.  A journal
/// generation is complete only after the owner adopts it into the reusable Pack
/// index; records left behind by an interrupted render are therefore unreachable.
/// </summary>
internal sealed class AudioCachePackJournalWriter : IDisposable
{
    private readonly string _directory;
    private FileStream? _stream;
    private int _journalIndex;
    private bool _completed;

    public AudioCachePackJournalWriter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        foreach (string stale in Directory.EnumerateFiles(
            _directory,
            AudioCachePackJournal.JournalFilePattern))
        {
            File.Delete(stale);
        }
        _stream = CreateNextJournal();
    }

    public void WriteBlock(
        string key,
        int blockIndex,
        int blockCount,
        long totalPayloadLength,
        ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        AudioCachePackStore.ValidateKey(key);
        if (blockIndex < 0 || blockIndex >= blockCount || blockCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIndex));
        }
        if (totalPayloadLength < 0 || payload.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalPayloadLength));
        }
        long recordLength = checked(AudioCachePackStore.EntryHeaderSize + payload.Length);
        if (_stream!.Length + recordLength > AudioCachePackStore.MaximumGenerationBytes)
        {
            // A completed generation can span multiple journal files.  Every
            // closed predecessor must be durable before the last file is
            // fsynced and the directory is adopted into the live Pack index.
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
            _stream = CreateNextJournal();
        }

        Span<byte> header = stackalloc byte[AudioCachePackStore.EntryHeaderSize];
        AudioCachePackJournal.WriteEntryHeader(
            header,
            key,
            blockIndex,
            blockCount,
            totalPayloadLength,
            payload);
        _stream.Write(header);
        _stream.Write(payload);
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (_completed)
        {
            return;
        }
        _stream!.Flush(flushToDisk: true);
        _completed = true;
    }

    public void Dispose()
    {
        FileStream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }
        if (!_completed)
        {
            stream.Flush(flushToDisk: false);
        }
        stream.Dispose();
    }

    private FileStream CreateNextJournal()
    {
        string path = AudioCachePackJournal.GetJournalPath(
            _directory,
            _journalIndex++);
        return AudioCachePackStore.CreatePackFile(path, generation: 0);
    }
}

internal static class AudioCachePackJournal
{
    public const string JournalFilePattern = "journal-*.mcap";

    public static string GetDirectoryPath(string stagingPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        return Path.GetFullPath(stagingPath) + ".journal";
    }

    public static string GetJournalPath(string directory, int index) =>
        Path.Combine(
            Path.GetFullPath(directory),
            $"journal-{index:D8}.mcap");

    public static void WriteEntryHeader(
        Span<byte> destination,
        string key,
        int blockIndex,
        int blockCount,
        long totalPayloadLength,
        ReadOnlySpan<byte> payload)
    {
        if (destination.Length < AudioCachePackStore.EntryHeaderSize)
        {
            throw new ArgumentException("The Pack entry header buffer is too small.", nameof(destination));
        }
        destination = destination[..AudioCachePackStore.EntryHeaderSize];
        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination, AudioCachePackStore.EntryMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], AudioCachePackStore.Version);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], blockIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], blockCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], totalPayloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], payload.Length);
        Convert.FromHexString(key).CopyTo(destination[32..64]);
        SHA256.HashData(payload, destination[64..96]);
    }
}
