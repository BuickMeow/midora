using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public readonly record struct SoundFontTarget(byte BankMsb, byte BankLsb, byte Program)
{
    public void Validate()
    {
        if (BankMsb > 127 || BankLsb > 127 || Program > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SoundFontTarget),
                "A SoundFont target Bank MSB, Bank LSB, and Program must each be from 0 through 127.");
        }
    }

    public override string ToString() => $"MSB {BankMsb} · LSB {BankLsb} · Program {Program}";
}

public sealed record SoundFontConfiguration(string Path, SoundFontTarget? Target)
{
    public bool IsSfz => string.Equals(
        System.IO.Path.GetExtension(Path),
        ".sfz",
        StringComparison.OrdinalIgnoreCase);

    public SoundFontConfiguration Normalize()
    {
        Validate();
        return this with { Path = System.IO.Path.GetFullPath(Path) };
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        if (!System.IO.Path.IsPathFullyQualified(Path)
            || Path.StartsWith("\\\\", StringComparison.Ordinal)
            || Path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A SoundFont path must be a fully-qualified local file path.",
                nameof(Path));
        }

        string extension = System.IO.Path.GetExtension(System.IO.Path.GetFullPath(Path));
        if (!string.Equals(extension, ".sf2", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".sfz", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Only .sf2 and .sfz SoundFont files are supported.",
                nameof(Path));
        }
        if (string.Equals(extension, ".sfz", StringComparison.OrdinalIgnoreCase)
            && Target is null)
        {
            throw new ArgumentException(
                "An SFZ SoundFont requires a target Bank MSB, Bank LSB, and Program.",
                nameof(Target));
        }
        Target?.Validate();
    }
}

public sealed class SoundFontSetDefinition
{
    private SoundFontSetDefinition(
        SoundFontConfiguration[] configurations,
        string cacheIdentity)
    {
        Configurations = Array.AsReadOnly(configurations);
        Paths = Array.AsReadOnly(configurations.Select(value => value.Path).ToArray());
        CacheIdentity = cacheIdentity;
    }

    public IReadOnlyList<SoundFontConfiguration> Configurations { get; }
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Identifies the ordered path/metadata snapshot for cache isolation. This is not a
    /// SoundFont content hash and does not validate any SF2 bytes.
    /// </summary>
    public string CacheIdentity { get; }

    public static SoundFontSetDefinition Create(IReadOnlyList<string> paths)
        => Create(paths.Select(path => new SoundFontConfiguration(path, null)).ToArray());

    public static SoundFontSetDefinition Create(
        IReadOnlyList<SoundFontConfiguration> configurations)
    {
        SoundFontConfiguration[] normalized = NormalizeConfigurations(configurations);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, normalized.Length);
        foreach (SoundFontConfiguration configuration in normalized)
        {
            string path = configuration.Path;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("An enabled SoundFont does not exist.", path);
            }

            FileInfo information = new(path);
            AppendConfiguration(hash, configuration);
            AppendInt64(hash, information.Length);
            AppendInt64(hash, information.LastWriteTimeUtc.Ticks);
        }

        return new(normalized, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Normalizes an application preference without opening, reading, hashing, or
    /// otherwise validating the referenced SoundFont files or SFZ dependencies.
    /// </summary>
    public static SoundFontSetDefinition CreateConfiguration(IReadOnlyList<string> paths)
        => CreateConfiguration(
            paths.Select(path => new SoundFontConfiguration(path, null)).ToArray());

    public static SoundFontSetDefinition CreateConfiguration(
        IReadOnlyList<SoundFontConfiguration> configurations)
    {
        SoundFontConfiguration[] normalized = NormalizeConfigurations(configurations);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendInt32(hash, normalized.Length);
        foreach (SoundFontConfiguration configuration in normalized)
        {
            AppendConfiguration(hash, configuration);
        }
        return new(normalized, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static SoundFontConfiguration[] NormalizeConfigurations(
        IReadOnlyList<SoundFontConfiguration> configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        if (configurations.Count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configurations),
                "An enabled SoundFont list must contain from 1 through 256 files.");
        }

        SoundFontConfiguration[] normalized = new SoundFontConfiguration[configurations.Count];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < configurations.Count; index++)
        {
            SoundFontConfiguration value = configurations[index]
                ?? throw new ArgumentNullException(nameof(configurations));
            SoundFontConfiguration configuration = value.Normalize();
            string path = configuration.Path;
            if (!unique.Add(path))
            {
                throw new ArgumentException(
                    "The enabled SoundFont list contains a duplicate path.",
                    nameof(configurations));
            }
            normalized[index] = configuration;
        }
        return normalized;
    }

    private static void AppendConfiguration(
        IncrementalHash hash,
        SoundFontConfiguration configuration)
    {
        AppendString(hash, configuration.Path);
        hash.AppendData([configuration.Target is null ? (byte)0 : (byte)1]);
        if (configuration.Target is SoundFontTarget target)
        {
            hash.AppendData([target.BankMsb, target.BankLsb, target.Program]);
        }
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
    private const int Version = 2;
    private const int MaximumSoundFontCount = 256;
    private const int MaximumPathCharacterCount = 32_767;

    public static void Write(
        string path,
        IReadOnlyList<SoundFontConfiguration> soundFonts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(soundFonts);
        if (soundFonts.Count is < 1 or > MaximumSoundFontCount)
        {
            throw new ArgumentOutOfRangeException(nameof(soundFonts));
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
        writer.Write(soundFonts.Count);
        foreach (SoundFontConfiguration value in soundFonts)
        {
            SoundFontConfiguration configuration = value.Normalize();
            string normalized = configuration.Path;
            if (normalized.Length > MaximumPathCharacterCount)
            {
                throw new InvalidDataException("A SoundFont path exceeds the supported length.");
            }
            writer.Write(normalized);
            writer.Write(configuration.Target is not null);
            if (configuration.Target is SoundFontTarget target)
            {
                writer.Write(target.BankMsb);
                writer.Write(target.BankLsb);
                writer.Write(target.Program);
            }
        }
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    public static SoundFontConfiguration[] Read(string path)
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
        SoundFontConfiguration[] result = new SoundFontConfiguration[count];
        for (int index = 0; index < count; index++)
        {
            string value = reader.ReadString();
            if (value.Length is 0 or > MaximumPathCharacterCount || !Path.IsPathFullyQualified(value))
            {
                throw new InvalidDataException("The SoundFont set descriptor contains an invalid path.");
            }
            SoundFontTarget? target = reader.ReadBoolean()
                ? new(reader.ReadByte(), reader.ReadByte(), reader.ReadByte())
                : null;
            try
            {
                result[index] = new SoundFontConfiguration(
                    Path.GetFullPath(value),
                    target).Normalize();
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The SoundFont set descriptor contains an invalid configuration.",
                    exception);
            }
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The SoundFont set descriptor has trailing data.");
        }
        return result;
    }
}
