using System.Collections.ObjectModel;

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

public enum TemplateEventMappingParameter
{
    Number,
    Value,
    SecondaryValue
}

public readonly record struct TemplateEventMappingTarget(
    TemplateEventKind EventKind,
    int EventNumber,
    TemplateEventMappingParameter Parameter)
{
    public static TemplateEventMappingTarget Create(
        TemplateEventKind eventKind,
        int eventNumber,
        TemplateEventMappingParameter parameter) =>
        new(
            eventKind,
            UsesEventNumber(eventKind) ? eventNumber : 0,
            parameter);

    public static IEnumerable<TemplateEventMappingTarget> Enumerate(TemplateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Number);
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.ControlChange:
            case TemplateEventKind.Program:
            case TemplateEventKind.PitchBend:
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb)
                {
                    yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                }
                if (value.HasBankLsb)
                {
                    yield return Create(
                        value.Kind,
                        value.Number,
                        TemplateEventMappingParameter.SecondaryValue);
                }
                break;
            case TemplateEventKind.PitchBendRange:
                yield return Create(value.Kind, value.Number, TemplateEventMappingParameter.Value);
                yield return Create(
                    value.Kind,
                    value.Number,
                    TemplateEventMappingParameter.SecondaryValue);
                break;
        }
    }

    public static bool IsSupported(TemplateEventMappingTarget target) =>
        Enum.IsDefined(target.EventKind)
        && Enum.IsDefined(target.Parameter)
        && target.EventNumber == (UsesEventNumber(target.EventKind) ? target.EventNumber : 0)
        && target.Parameter switch
        {
            TemplateEventMappingParameter.Number => target.EventKind == TemplateEventKind.Note,
            TemplateEventMappingParameter.Value => true,
            TemplateEventMappingParameter.SecondaryValue =>
                target.EventKind is TemplateEventKind.Bank or TemplateEventKind.PitchBendRange,
            _ => false
        };

    private static bool UsesEventNumber(TemplateEventKind eventKind) =>
        eventKind is TemplateEventKind.ControlChange
            or TemplateEventKind.RegisteredParameter
            or TemplateEventKind.NonRegisteredParameter;
}

public sealed class SubVoiceEventMapping
{
    public SubVoiceEventMapping(MidoraProject project, TemplateEventMappingTarget target)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        Target = target;
        Steps = new MappingChain(project);
    }

    internal SubVoiceEventMapping(
        MidoraProject project,
        TemplateEventMappingTarget target,
        MidoraId mappingChainId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        Target = target;
        Steps = new MappingChain(project, mappingChainId);
    }

    public TemplateEventMappingTarget Target { get; }
    public MappingChain Steps { get; internal set; }
    public MidiIntegerTargetSettings TargetSettings { get; } = new();
}

public sealed class TemplateEvent
{
    public TemplateEvent(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Id = project.AllocateStableId();
    }

    internal TemplateEvent(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        _project = project;
        Id = preservedId;
    }

    private readonly MidoraProject _project;
    private readonly Dictionary<TemplateEventMappingParameter, SubVoiceEventMapping> _detachedMappings = [];
    private SubVoice? _owner;

    public MidoraId Id { get; init; }
    public TemplateEventKind Kind { get; set; }
    public long Tick { get; set; }
    public long LengthTicks { get; set; }
    public int Number { get; set; }
    public int Value { get; set; }
    public int SecondaryValue { get; set; }
    public bool HasBankMsb { get; internal set; } = true;
    public bool HasBankLsb { get; internal set; } = true;
    public bool FollowPitchDelta { get; set; } = true;
    public MappingChain NumberMappings
    {
        get => GetMapping(TemplateEventMappingParameter.Number).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.Number).Steps = value;
    }

    public MappingChain ValueMappings
    {
        get => GetMapping(TemplateEventMappingParameter.Value).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.Value).Steps = value;
    }

    public MappingChain SecondaryValueMappings
    {
        get => GetMapping(TemplateEventMappingParameter.SecondaryValue).Steps;
        internal set => GetMapping(TemplateEventMappingParameter.SecondaryValue).Steps = value;
    }

    public MidiIntegerTargetSettings NumberTargetSettings =>
        GetMapping(TemplateEventMappingParameter.Number).TargetSettings;

    public MidiIntegerTargetSettings ValueTargetSettings =>
        GetMapping(TemplateEventMappingParameter.Value).TargetSettings;

    public MidiIntegerTargetSettings SecondaryValueTargetSettings =>
        GetMapping(TemplateEventMappingParameter.SecondaryValue).TargetSettings;

    internal bool AttachTo(SubVoice owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_owner is not null && !ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "A Template Event cannot be attached to more than one SubVoice.");
        }
        bool newlyAttached = _owner is null;
        _owner = owner;
        foreach (SubVoiceEventMapping detached in _detachedMappings.Values)
        {
            SubVoiceEventMapping? existing = owner.FindEventMapping(detached.Target);
            if (existing is null)
            {
                owner.EventMappings.Add(detached);
                continue;
            }
            MergeDetachedMapping(existing, detached);
        }
        _detachedMappings.Clear();
        return newlyAttached;
    }

    internal void EnsureMappings() => _owner?.EnsureEventMappings(this, createOptional: false);

    private SubVoiceEventMapping GetMapping(TemplateEventMappingParameter parameter)
    {
        if (_owner is null)
        {
            if (_detachedMappings.TryGetValue(parameter, out SubVoiceEventMapping? detached))
            {
                return detached;
            }
            TemplateEventMappingTarget target = TemplateEventMappingTarget.Create(
                Kind,
                Number,
                parameter);
            if (!TemplateEventMappingTarget.IsSupported(target))
            {
                throw new InvalidOperationException(
                    "The Template Event does not expose the requested Mapping target.");
            }
            detached = new SubVoiceEventMapping(_project, target);
            _detachedMappings.Add(parameter, detached);
            return detached;
        }
        return _owner.GetOrCreateEventMapping(
            TemplateEventMappingTarget.Create(Kind, Number, parameter));
    }

    private static void MergeDetachedMapping(
        SubVoiceEventMapping existing,
        SubVoiceEventMapping detached)
    {
        bool detachedHasContent = detached.Steps.Count != 0
            || !detached.Steps.IsEnabled
            || detached.TargetSettings.Rounding != MappingRounding.Round
            || detached.TargetSettings.Overflow != MappingOverflow.Fail;
        if (!detachedHasContent)
        {
            return;
        }
        bool existingHasContent = existing.Steps.Count != 0
            || !existing.Steps.IsEnabled
            || existing.TargetSettings.Rounding != MappingRounding.Round
            || existing.TargetSettings.Overflow != MappingOverflow.Fail;
        if (existingHasContent)
        {
            throw new InvalidOperationException(
                "A shared SubVoice event Mapping already exists for this target.");
        }
        existing.Steps = detached.Steps;
        existing.TargetSettings.Rounding = detached.TargetSettings.Rounding;
        existing.TargetSettings.Overflow = detached.TargetSettings.Overflow;
    }

    public static TemplateEvent Note(
        MidoraProject project,
        long tick,
        long lengthTicks,
        int note,
        int velocity) => new(project)
        {
            Kind = TemplateEventKind.Note,
            Tick = tick,
            LengthTicks = lengthTicks,
            Number = note,
            Value = velocity
        };

    public static TemplateEvent ControlChange(
        MidoraProject project,
        long tick,
        int controller,
        int value) => new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = tick,
            Number = controller,
            Value = value
        };

    public static TemplateEvent Program(MidoraProject project, long tick, int program) => new(project)
    {
        Kind = TemplateEventKind.Program,
        Tick = tick,
        Value = program
    };

    public static TemplateEvent Bank(MidoraProject project, long tick, int? msb, int? lsb) => new(project)
    {
        Kind = TemplateEventKind.Bank,
        Tick = tick,
        Value = msb ?? 0,
        SecondaryValue = lsb ?? 0,
        HasBankMsb = msb.HasValue,
        HasBankLsb = lsb.HasValue
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
    public SubVoice(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Id = project.AllocateStableId();
        Events = new TemplateEventCollection(this);
    }

    internal SubVoice(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        _project = project;
        Id = preservedId;
        Events = new TemplateEventCollection(this);
    }

    private readonly MidoraProject _project;

    public MidoraId Id { get; init; }
    public string? Name { get; set; }
    public int? RootNoteOverride { get; set; }
    public MidiInitialState InitialState { get; } = new();
    public TemplateEventCollection Events { get; }
    public List<SubVoiceEventMapping> EventMappings { get; } = [];
    public List<ValueCurve> Curves { get; } = [];

    public SubVoiceEventMapping? FindEventMapping(TemplateEventMappingTarget target) =>
        EventMappings.SingleOrDefault(value => value.Target == target);

    public SubVoiceEventMapping GetOrCreateEventMapping(TemplateEventMappingTarget target)
    {
        if (!TemplateEventMappingTarget.IsSupported(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }
        SubVoiceEventMapping? existing = FindEventMapping(target);
        if (existing is not null)
        {
            return existing;
        }
        SubVoiceEventMapping created = new(_project, target);
        EventMappings.Add(created);
        return created;
    }

    internal void EnsureEventMappings(TemplateEvent value, bool createOptional)
    {
        foreach (TemplateEventMappingTarget target in TemplateEventMappingTarget.Enumerate(value))
        {
            bool mandatory = target.EventKind == TemplateEventKind.Note;
            // Optional non-Note owners are created only for a genuinely new
            // target. Existing raw events with no owner represent an explicit
            // Mapping deletion and must remain raw during edits/reinsertion.
            if (!mandatory && (!createOptional || Events.Any(existing =>
                    TemplateEventMappingTarget.Enumerate(existing).Contains(target))))
            {
                continue;
            }
            _ = GetOrCreateEventMapping(target);
        }
    }
}

public sealed class TemplateEventCollection : Collection<TemplateEvent>
{
    private readonly SubVoice _owner;
    private bool _suppressOptionalMappingCreation;

    internal TemplateEventCollection(SubVoice owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public void AddRange(IEnumerable<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (TemplateEvent value in values)
        {
            Add(value);
        }
    }

    internal void AddRangeWithoutOptionalMappingCreation(IEnumerable<TemplateEvent> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        bool oldValue = _suppressOptionalMappingCreation;
        _suppressOptionalMappingCreation = true;
        try
        {
            AddRange(values);
        }
        finally
        {
            _suppressOptionalMappingCreation = oldValue;
        }
    }

    internal void AddWithoutOptionalMappingCreation(TemplateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        bool oldValue = _suppressOptionalMappingCreation;
        _suppressOptionalMappingCreation = true;
        try
        {
            Add(value);
        }
        finally
        {
            _suppressOptionalMappingCreation = oldValue;
        }
    }

    protected override void InsertItem(int index, TemplateEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool newlyAttached = item.AttachTo(_owner);
        _owner.EnsureEventMappings(
            item,
            createOptional: newlyAttached && !_suppressOptionalMappingCreation);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, TemplateEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool newlyAttached = item.AttachTo(_owner);
        _owner.EnsureEventMappings(
            item,
            createOptional: newlyAttached && !_suppressOptionalMappingCreation);
        base.SetItem(index, item);
    }
}

public sealed class InstrumentEnvelope
{
    public InstrumentEnvelope(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal InstrumentEnvelope(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
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
    public EventInstrument(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal EventInstrument(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
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
    public List<MidoraId> LogicalTrackIds { get; } = [];
    public List<LogicalParameterDefinition> LogicalParameters { get; } = [];
    public List<SubVoice> SubVoices { get; } = [];
    public List<InstrumentEnvelope> Envelopes { get; } = [];
    public List<CSharpMappingFunction> MappingFunctions { get; } = [];
    public List<LogicalParameterMapping> ParameterMappings { get; } = [];
}
