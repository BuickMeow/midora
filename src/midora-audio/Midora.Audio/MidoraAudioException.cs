namespace Midora.Audio;

public class MidoraAudioException : Exception
{
    public MidoraAudioException(string message) : base(message)
    {
    }

    public MidoraAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
