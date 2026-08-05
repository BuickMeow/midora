namespace Midora.Domain;

public readonly record struct MidoraId(Guid Value) : IComparable<MidoraId>
{
    public static MidoraId New() => new(Guid.NewGuid());

    public int CompareTo(MidoraId other) => Value.CompareTo(other.Value);

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

public sealed record CurvePoint(long Tick, double Value, CurveInterpolation Interpolation = CurveInterpolation.Linear)
{
    public MidoraId Id { get; init; } = MidoraId.New();
}

public sealed class ValueCurve
{
    public MidoraId Id { get; init; } = MidoraId.New();
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
