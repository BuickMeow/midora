namespace Midora.Audio;

public interface ISoundfontFactory
{
    ISoundfont Create(string filePath);
}
