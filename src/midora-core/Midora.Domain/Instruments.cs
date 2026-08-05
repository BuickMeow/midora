namespace Midora.Domain;

public enum ShortNoteLifecycle
{
    CutAtNoteOff,
    OneShot,
    Tail
}

public enum LongNoteLifecycle
{
    HoldLastState,
    EndAtTemplate
}

public enum OverlapPolicy
{
    Reject,
    Warn,
    LetOverlap,
    CutPrevious,
    CutNewRejectNew
}

public enum OverlapScope
{
    SamePitch,
    AnyPitch
}

public enum TemplateEventKind
{
    Note,
    ControlChange,
    Bank,
    Program,
    PitchBend,
    RegisteredParameter,
    NonRegisteredParameter,
    PitchBendRange
}

public sealed class TemplateEvent
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public TemplateEventKind Kind { get; set; }
    public long Tick { get; set; }
    public long LengthTicks { get; set; }
    public int Number { get; set; }
    public int Value { get; set; }
    public int SecondaryValue { get; set; }
    public bool FollowPitchDelta { get; set; } = true;
    public MappingChain NumberMappings { get; } = new();
    public MappingChain ValueMappings { get; } = new();
    public MappingChain SecondaryValueMappings { get; } = new();

    public static TemplateEvent Note(long tick, long lengthTicks, int note, int velocity) => new()
    {
        Kind = TemplateEventKind.Note,
        Tick = tick,
        LengthTicks = lengthTicks,
        Number = note,
        Value = velocity
    };

    public static TemplateEvent ControlChange(long tick, int controller, int value) => new()
    {
        Kind = TemplateEventKind.ControlChange,
        Tick = tick,
        Number = controller,
        Value = value
    };

    public static TemplateEvent Program(long tick, int program) => new()
    {
        Kind = TemplateEventKind.Program,
        Tick = tick,
        Value = program
    };

    public static TemplateEvent Bank(long tick, int msb, int lsb) => new()
    {
        Kind = TemplateEventKind.Bank,
        Tick = tick,
        Value = msb,
        SecondaryValue = lsb
    };
}

public sealed class MidiInitialState
{
    public int? BankMsb { get; set; }
    public int? BankLsb { get; set; }
    public int? Program { get; set; }
    public int? PitchBend { get; set; }
    public int? PitchBendRangeSemitones { get; set; }
    public int? PitchBendRangeCents { get; set; }
    public Dictionary<int, int> Controllers { get; } = [];
    public Dictionary<int, int> RegisteredParameters { get; } = [];
    public Dictionary<int, int> NonRegisteredParameters { get; } = [];

    public MidiInitialState Clone()
    {
        MidiInitialState result = new()
        {
            BankMsb = BankMsb,
            BankLsb = BankLsb,
            Program = Program,
            PitchBend = PitchBend,
            PitchBendRangeSemitones = PitchBendRangeSemitones,
            PitchBendRangeCents = PitchBendRangeCents
        };
        foreach ((int controller, int value) in Controllers)
        {
            result.Controllers.Add(controller, value);
        }
        foreach ((int parameter, int value) in RegisteredParameters)
        {
            result.RegisteredParameters.Add(parameter, value);
        }
        foreach ((int parameter, int value) in NonRegisteredParameters)
        {
            result.NonRegisteredParameters.Add(parameter, value);
        }

        return result;
    }
}

public sealed class SubVoice
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public string? Name { get; set; }
    public int? RootNoteOverride { get; set; }
    public MidiInitialState InitialState { get; } = new();
    public List<TemplateEvent> Events { get; } = [];
    public List<ValueCurve> Curves { get; } = [];
}

public sealed class InstrumentEnvelope
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public string? Name { get; set; }
    public long DelayTicks { get; set; }
    public long AttackTicks { get; set; }
    public long HoldTicks { get; set; }
    public long DecayTicks { get; set; }
    public double StartValue { get; set; }
    public double PeakValue { get; set; } = 1;
    public double SustainValue { get; set; } = 1;
    public long ReleaseTicks { get; set; }
    public double EndValue { get; set; }
}

public sealed class EventInstrument
{
    public MidoraId Id { get; init; } = MidoraId.New();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public MidoraColor Color { get; set; } = MidoraColor.DefaultInstrument;
    public MidoraId? LibraryFolderId { get; set; }
    public int RootNote { get; set; } = 60;
    public long TemplateLengthTicks { get; set; }
    public bool RequiresChannelIsolation { get; set; }
    public OverlapPolicy OverlapPolicy { get; set; } = OverlapPolicy.Reject;
    public OverlapScope OverlapScope { get; set; } = OverlapScope.SamePitch;
    public ShortNoteLifecycle ShortLifecycle { get; set; } = ShortNoteLifecycle.CutAtNoteOff;
    public LongNoteLifecycle LongLifecycle { get; set; } = LongNoteLifecycle.HoldLastState;
    public long? LoopStartTick { get; set; }
    public long? LoopEndTick { get; set; }
    public MidiInitialState InitialState { get; } = new();
    public List<LogicalParameterDefinition> LogicalParameters { get; } = [];
    public List<SubVoice> SubVoices { get; } = [];
    public List<InstrumentEnvelope> Envelopes { get; } = [];
    public List<CSharpMappingFunction> MappingFunctions { get; } = [];
    public List<LogicalParameterMapping> ParameterMappings { get; } = [];
}
