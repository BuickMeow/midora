using Midora.Audio;

namespace Midora.AudioRender;

public enum AudioRenderSoundFontFailure
{
    NoEnabledSoundFonts,
    Missing,
    Unreadable
}

public sealed class AudioRenderSoundFontException : Exception
{
    internal AudioRenderSoundFontException(
        AudioRenderSoundFontFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Failure = failure;

    public AudioRenderSoundFontFailure Failure { get; }
}

/// <summary>
/// Freezes only the ordered application-level SoundFont path set and its cheap
/// file-metadata cache identity. It never copies or hashes SF2 content.
/// </summary>
public sealed class AudioRenderSoundFontSnapshot : IDisposable, IAsyncDisposable
{
    private AudioRenderSoundFontSnapshot(SoundFontSetDefinition definition)
    {
        Definition = definition;
        SoundFontPaths = Array.AsReadOnly(definition.Paths.ToArray());
        CacheIdentity = definition.CacheIdentity;
        TotalFileSizeBytes = definition.Paths.Sum(value => new FileInfo(value).Length);
        Diagnostics = Array.Empty<AudioRenderDiagnostic>();
    }

    public SoundFontSetDefinition Definition { get; }
    public IReadOnlyList<string> SoundFontPaths { get; }
    public string CacheIdentity { get; }
    public long TotalFileSizeBytes { get; }
    public IReadOnlyList<AudioRenderDiagnostic> Diagnostics { get; }

    public static Task<AudioRenderSoundFontSnapshot> CreateAsync(
        IReadOnlyList<string> soundFontPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(soundFontPaths);
        cancellationToken.ThrowIfCancellationRequested();
        if (soundFontPaths.Count == 0)
        {
            throw new AudioRenderSoundFontException(
                AudioRenderSoundFontFailure.NoEnabledSoundFonts,
                "Audio rendering requires at least one enabled application SoundFont.");
        }

        try
        {
            return Task.FromResult(new AudioRenderSoundFontSnapshot(
                SoundFontSetDefinition.Create(soundFontPaths)));
        }
        catch (FileNotFoundException exception)
        {
            throw new AudioRenderSoundFontException(
                AudioRenderSoundFontFailure.Missing,
                exception.Message,
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AudioRenderSoundFontException(
                AudioRenderSoundFontFailure.Unreadable,
                "An enabled application SoundFont is unavailable.",
                exception);
        }
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
