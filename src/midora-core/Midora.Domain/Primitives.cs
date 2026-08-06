using System.Buffers.Binary;
using System.Globalization;

namespace Midora.Domain;

public readonly record struct MidoraId(Guid Value) : IComparable<MidoraId>
{
    public static MidoraId FromSequence(UInt128 sequence)
    {
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Stable ID sequence zero is reserved.");
        }
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)(sequence >> 64));
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], (ulong)sequence);
        return new(new Guid(bytes, bigEndian: true));
    }

    public static MidoraId FromParts(ulong high, ulong low)
        => FromSequence(((UInt128)high << 64) | low);

    public static bool TryParseCanonical(string? value, out MidoraId id)
    {
        id = default;
        if (value is not { Length: 32 })
        {
            return false;
        }
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        if (!UInt128.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out UInt128 sequence)
            || sequence == 0)
        {
            return false;
        }
        id = FromSequence(sequence);
        return true;
    }

    public UInt128 ToSequence()
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = Value.TryWriteBytes(bytes, bigEndian: true, out _);
        return ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    public ulong High => (ulong)(ToSequence() >> 64);
    public ulong Low => (ulong)ToSequence();

    public int CompareTo(MidoraId other) => ToSequence().CompareTo(other.ToSequence());

    public override string ToString() => ToSequence().ToString("x32", CultureInfo.InvariantCulture);
}

public readonly record struct TickRange(long StartTick, long EndTick)
{
    public long Length => EndTick - StartTick;
    public bool IsValid => StartTick >= 0 && EndTick >= StartTick;
    public bool Contains(long tick) => tick >= StartTick && tick < EndTick;
    public bool Intersects(TickRange other) => StartTick < other.EndTick && other.StartTick < EndTick;
}

public readonly record struct MidoraColor(byte Red, byte Green, byte Blue)
{
    public static MidoraColor DefaultInstrument { get; } = new(0x6b, 0x72, 0x80);
}

public enum CurveInterpolation
{
    Step,
    Linear
}

public sealed record CurvePoint
{
    public CurvePoint(
        MidoraProject project,
        long tick,
        double value,
        CurveInterpolation interpolation = CurveInterpolation.Linear)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        Value = value;
        Interpolation = interpolation;
    }

    internal CurvePoint(
        MidoraProject project,
        MidoraId preservedId,
        long tick,
        double value,
        CurveInterpolation interpolation)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        Value = value;
        Interpolation = interpolation;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public double Value { get; init; }
    public CurveInterpolation Interpolation { get; init; }
}

public sealed class ValueCurve
{
    public ValueCurve(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        TargetSettings = new MidiIntegerTargetSettings();
    }

    internal ValueCurve(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        TargetSettings = new MidiIntegerTargetSettings();
    }

    public MidoraId Id { get; init; }
    public MidiValueTarget Target { get; set; }
    public MidiIntegerTargetSettings TargetSettings { get; }
    public List<CurvePoint> Points { get; } = [];
}

public enum MidiValueKind
{
    ControlChange,
    BankMsb,
    BankLsb,
    Program,
    PitchBend,
    RegisteredParameter,
    NonRegisteredParameter,
    PitchBendRangeSemitones,
    PitchBendRangeCents
}

public readonly record struct MidiValueTarget(MidiValueKind Kind, int Number = 0)
{
    public static MidiValueTarget ControlChange(int controller) => new(MidiValueKind.ControlChange, controller);
    public static MidiValueTarget BankMsb => new(MidiValueKind.BankMsb);
    public static MidiValueTarget BankLsb => new(MidiValueKind.BankLsb);
    public static MidiValueTarget Program => new(MidiValueKind.Program);
    public static MidiValueTarget PitchBend => new(MidiValueKind.PitchBend);
    public static MidiValueTarget Rpn(int parameter) => new(MidiValueKind.RegisteredParameter, parameter);
    public static MidiValueTarget Nrpn(int parameter) => new(MidiValueKind.NonRegisteredParameter, parameter);
    public static MidiValueTarget PitchBendRange => PitchBendRangeSemitones;
    public static MidiValueTarget PitchBendRangeSemitones => new(MidiValueKind.PitchBendRangeSemitones);
    public static MidiValueTarget PitchBendRangeCents => new(MidiValueKind.PitchBendRangeCents);
}
