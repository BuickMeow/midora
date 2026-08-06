namespace Midora.Persistence;

internal sealed class ProtobufObjectHeaderExceptionV1 : IOException
{
    public ProtobufObjectHeaderExceptionV1(string message)
        : base(message)
    {
    }
}
