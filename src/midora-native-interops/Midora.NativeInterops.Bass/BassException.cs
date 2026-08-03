namespace Midora.NativeInterops.Bass;

public class BassException : Exception
{
    public BassException() : base($"BASS Error: code = {BASS.ErrorGetCode()}") { }
}
