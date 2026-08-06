using System.Globalization;
using System.Text;

namespace Midora.Domain;

public enum SoundFontReferenceMode
{
    External,
    Embedded
}

public abstract record ProjectSoundFontReference
{
    protected ProjectSoundFontReference(
        string originalFileName,
        string sha256,
        long fileSizeBytes)
    {
        SoundFontReferenceValidation.ValidateOriginalFileName(originalFileName);
        SoundFontReferenceValidation.ValidateSha256(sha256);
        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }

        OriginalFileName = originalFileName;
        Sha256 = sha256;
        FileSizeBytes = fileSizeBytes;
    }

    public abstract SoundFontReferenceMode Mode { get; }
    public string OriginalFileName { get; }
    public string Sha256 { get; }
    public long FileSizeBytes { get; }
}

public sealed record ExternalProjectSoundFontReference : ProjectSoundFontReference
{
    public ExternalProjectSoundFontReference(
        string relativePath,
        string originalFileName,
        string sha256,
        long fileSizeBytes)
        : base(originalFileName, sha256, fileSizeBytes)
    {
        SoundFontReferenceValidation.ValidateExternalRelativePath(relativePath);
        string pathFileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        if (!string.Equals(pathFileName, originalFileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "External SoundFont original file name must exactly match the stored relative path.",
                nameof(originalFileName));
        }
        RelativePath = relativePath;
    }

    public override SoundFontReferenceMode Mode => SoundFontReferenceMode.External;
    public string RelativePath { get; }
}

public sealed record EmbeddedProjectSoundFontReference : ProjectSoundFontReference
{
    public EmbeddedProjectSoundFontReference(
        MidoraId resourceId,
        string originalFileName,
        string sha256,
        long fileSizeBytes)
        : base(originalFileName, sha256, fileSizeBytes)
    {
        if (resourceId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceId));
        }
        ResourceId = resourceId;
    }

    public override SoundFontReferenceMode Mode => SoundFontReferenceMode.Embedded;
    public MidoraId ResourceId { get; }
}

public sealed class ProjectSoundFontSettings
{
    public ProjectSoundFontReference? Reference { get; private set; }

    public void Clear() => Reference = null;

    public void SetReference(ProjectSoundFontReference? reference)
    {
        if (reference is not null
            and not ExternalProjectSoundFontReference
            and not EmbeddedProjectSoundFontReference)
        {
            throw new ArgumentException(
                "The Project SoundFont reference kind is not supported by this release.",
                nameof(reference));
        }
        Reference = reference;
    }

    public void SetExternal(
        string relativePath,
        string originalFileName,
        string sha256,
        long fileSizeBytes) =>
        Reference = new ExternalProjectSoundFontReference(
            relativePath, originalFileName, sha256, fileSizeBytes);

    public MidoraId SetEmbedded(
        MidoraProject project,
        string originalFileName,
        string sha256,
        long fileSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        SoundFontReferenceValidation.ValidateOriginalFileName(originalFileName);
        SoundFontReferenceValidation.ValidateSha256(sha256);
        if (fileSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileSizeBytes));
        }
        MidoraId resourceId = project.AllocateStableId();
        Reference = new EmbeddedProjectSoundFontReference(
            resourceId, originalFileName, sha256, fileSizeBytes);
        return resourceId;
    }

    internal void Restore(ProjectSoundFontReference? reference) => SetReference(reference);
}

public static class SoundFontReferenceValidation
{
    public const int MaximumPathScalars = 4_096;
    public const int MaximumFileNameScalars = 256;

    public static void ValidateExternalRelativePath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUnicode(value, nameof(value), MaximumPathScalars, allowEmpty: false);
        string[] segments = value.Split('/');
        bool rootFile = segments.Length == 1;
        bool soundFontsFile = segments.Length == 2
            && string.Equals(segments[0], "soundfonts", StringComparison.OrdinalIgnoreCase);
        if (!rootFile && !soundFontsFile
            || segments.Any(segment => segment.Length == 0 || segment is "." or "..")
            || value.Contains('\\')
            || value.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
            || value[0] == '/'
            || !value.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "External SoundFont paths must be an SF2 file in the Project directory or its direct soundfonts subdirectory.",
                nameof(value));
        }
        ValidateOriginalFileName(segments[^1]);
    }

    public static void ValidateOriginalFileName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUnicode(value, nameof(value), MaximumFileNameScalars, allowEmpty: false);
        if (value is "." or ".."
            || value.Length <= ".sf2".Length
            || value.Contains('/')
            || value.Contains('\\')
            || value.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0
            || !value.EndsWith(".sf2", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SoundFont original file name must be an SF2 base file name.", nameof(value));
        }
    }

    public static void ValidateSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("SoundFont SHA-256 must be 64 lowercase hexadecimal characters.", nameof(value));
        }
    }

    private static void ValidateUnicode(string value, string fieldName, int maximum, bool allowEmpty)
    {
        int count;
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(value);
            count = value.EnumerateRunes().Count();
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"{fieldName} must contain valid Unicode.", fieldName, exception);
        }
        if (!allowEmpty && count == 0 || count > maximum)
        {
            throw new ArgumentOutOfRangeException(fieldName);
        }
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.Value == 0 || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                throw new ArgumentException($"{fieldName} contains a forbidden control character.", fieldName);
            }
        }
    }
}
