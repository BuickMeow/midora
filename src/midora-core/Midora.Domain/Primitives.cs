using System.Buffers.Binary;

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

    public UInt128 ToSequence()
    {
        Span<byte> bytes = stackalloc byte[16];
        _ = Value.TryWriteBytes(bytes, bigEndian: true, out _);
        return ((UInt128)BinaryPrimitives.ReadUInt64BigEndian(bytes) << 64)
            | BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    public int CompareTo(MidoraId other) => ToSequence().CompareTo(other.ToSequence());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct TickRange(long StartTick, long EndTick)
{
    public long Length => EndTick - StartTick;
    public bool IsValid => StartTick >= 0 && EndTick >= StartTick;
    public bool Contains(long tick) => tick >= StartTick && tick < EndTick;
    public bool Intersects(TickRange other) => StartTick < other.EndTick && other.StartTick < EndTick;
}

public readonly record struct MidoraColor(uint Argb)
{
    public static MidoraColor DefaultInstrument { get; } = new(0xff6b7280);
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
    }

    public MidoraId Id { get; init; }
    public MidiValueTarget Target { get; set; }
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
