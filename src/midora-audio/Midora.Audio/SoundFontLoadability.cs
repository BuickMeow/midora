namespace Midora.Audio;

public enum SoundFontLoadabilityFailure
{
    Missing,
    Unreadable,
    UnsupportedOrCorrupt
}

public sealed class SoundFontLoadabilityException : Exception
{
    public SoundFontLoadabilityException(
        SoundFontLoadabilityFailure failure,
        string message,
        int? backendErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        BackendErrorCode = backendErrorCode;
    }

    public SoundFontLoadabilityFailure Failure { get; }
    public int? BackendErrorCode { get; }
}

public interface ISoundFontLoadabilityValidator
{
    ValueTask ValidateAsync(
        string soundFontPath,
        CancellationToken cancellationToken = default);
}
