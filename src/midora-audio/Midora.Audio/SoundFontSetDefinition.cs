using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public sealed class SoundFontSetDefinition
{
    private SoundFontSetDefinition(string[] paths, string cacheIdentity)
    {
        Paths = Array.AsReadOnly(paths);
        CacheIdentity = cacheIdentity;
    }

    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Identifies the ordered path/metadata snapshot for cache isolation. This is not a
    /// SoundFont content hash and does not validate any SF2 bytes.
    /// </summary>
    public string CacheIdentity { get; }

    public static SoundFontSetDefinition Create(IReadOnlyList<string> paths)
    {
        string[] normalized = NormalizePaths(paths);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, normalized.Length);
        foreach (string path in normalized)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("An enabled SoundFont does not exist.", path);
            }

            FileInfo information = new(path);
            AppendString(hash, path);
            AppendInt64(hash, information.Length);
            AppendInt64(hash, information.LastWriteTimeUtc.Ticks);
        }

        return new(normalized, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Normalizes an application preference without opening, reading, hashing, or
    /// otherwise validating the referenced SF2 files.
    /// </summary>
    public static SoundFontSetDefinition CreateConfiguration(IReadOnlyList<string> paths)
    {
        string[] normalized = NormalizePaths(paths);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, normalized.Length);
        foreach (string path in normalized)
        {
            AppendString(hash, path);
        }
        return new(normalized, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static string[] NormalizePaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(paths),
                "An enabled SoundFont list must contain from 1 through 256 files.");
        }

        string[] normalized = new string[paths.Count];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < paths.Count; index++)
        {
            string value = paths[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(paths));
            if (!Path.IsPathFullyQualified(value)
                || value.StartsWith("\\\\", StringComparison.Ordinal)
                || value.StartsWith("//", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "SoundFont paths must be fully-qualified local file paths.",
                    nameof(paths));
            }
            string path = Path.GetFullPath(value);
            if (!string.Equals(Path.GetExtension(path), ".sf2", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only .sf2 SoundFont files are supported.", nameof(paths));
            }
            if (!unique.Add(path))
            {
                throw new ArgumentException("The enabled SoundFont list contains a duplicate path.", nameof(paths));
            }
            normalized[index] = path;
        }
        return normalized;
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}

public static class SoundFontSetFile
{
    private const int Magic = 0x4653414D; // MASF
    private const int Version = 1;
    private const int MaximumSoundFontCount = 256;
    private const int MaximumPathCharacterCount = 32_767;

    public static void Write(string path, IReadOnlyList<string> soundFontPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(soundFontPaths);
        if (soundFontPaths.Count is < 1 or > MaximumSoundFontCount)
        {
            throw new ArgumentOutOfRangeException(nameof(soundFontPaths));
        }

        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.SequentialScan);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(soundFontPaths.Count);
        foreach (string soundFontPath in soundFontPaths)
        {
            string normalized = Path.GetFullPath(soundFontPath);
            if (normalized.Length > MaximumPathCharacterCount)
            {
                throw new InvalidDataException("A SoundFont path exceeds the supported length.");
            }
            writer.Write(normalized);
        }
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static string[] Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException("The SoundFont set descriptor has an unsupported format.");
        }
        int count = reader.ReadInt32();
        if (count is < 1 or > MaximumSoundFontCount)
        {
            throw new InvalidDataException("The SoundFont set descriptor count is invalid.");
        }
        string[] result = new string[count];
        for (int index = 0; index < count; index++)
        {
            string value = reader.ReadString();
            if (value.Length is 0 or > MaximumPathCharacterCount || !Path.IsPathFullyQualified(value))
            {
                throw new InvalidDataException("The SoundFont set descriptor contains an invalid path.");
            }
            result[index] = Path.GetFullPath(value);
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The SoundFont set descriptor has trailing data.");
        }
        return result;
    }
}
