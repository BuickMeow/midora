using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence;

internal static class ProtobufValueCodecV1
{
    public static long ToWire(MidoraId value)
    {
        if (value == default)
        {
            throw new InvalidDataException("Stable ID zero is reserved.");
        }
        return value.Value;
    }

    public static MidoraId FromWire(long value, string fieldName)
    {
        PersistenceValueValidationV1.ValidateStableId(value, fieldName);
        return new MidoraId(value);
    }

    public static RgbColor ToWire(MidoraColor value) => new()
    {
        Red = value.Red,
        Green = value.Green,
        Blue = value.Blue
    };

    public static MidoraColor FromWire(RgbColor? value, string fieldName)
    {
        if (value is null)
        {
            throw new InvalidDataException($"{fieldName} is required.");
        }
        PersistenceValueValidationV1.ValidateRgb(value.Red, value.Green, value.Blue, fieldName);
        return new MidoraColor((byte)value.Red, (byte)value.Green, (byte)value.Blue);
    }

    public static void Require(bool condition, string fieldName)
    {
        if (!condition)
        {
            throw new InvalidDataException($"{fieldName} is required.");
        }
    }

    public static void RequireFinite(double value, string fieldName)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"{fieldName} must be finite.");
        }
    }
}
