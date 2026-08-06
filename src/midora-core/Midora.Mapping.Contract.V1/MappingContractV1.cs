namespace Midora.Mapping.Contract.V1;

public static class MappingAbiV1
{
    public const int Version = 1;
    public const string NetCoreReferencePackVersion = "10.0.10";
    public const string CompilerProfileId = "midora-csharp14-net10.0.10-roslyn5.3-v1";
}

public readonly record struct MappingStableIdV1(ulong High, ulong Low)
{
    public bool IsEmpty => High == 0 && Low == 0;

    public override string ToString() => $"{High:x16}{Low:x16}";
}

public enum MappingTargetParameterV1
{
    Unknown = 0,
    Number = 1,
    Value = 2,
    SecondaryValue = 3,
    LogicalParameterOutput = 4
}

public enum MappingEventKindV1
{
    Unknown = 0,
    Note = 1,
    ControlChange = 2,
    BankSelect = 3,
    ProgramChange = 4,
    PitchBend = 5,
    Rpn = 6,
    Nrpn = 7,
    PitchBendRange = 8
}

public readonly record struct MappingContextV1(
    double CurrentValue,
    int TriggerNote,
    int TriggerVelocity,
    long GateLength,
    int PitchDelta,
    long TemplateTick,
    long ProjectTick,
    int TemplateNote,
    int TemplateVelocity)
{
    public int EffectiveRootNote { get; init; }
    public MappingStableIdV1 CurrentEventId { get; init; }
    public MappingTargetParameterV1 CurrentParameter { get; init; }
    public MappingEventKindV1 CurrentEventKind { get; init; }
    public MappingStableIdV1 LogicalParameterId { get; init; }
    public string? LogicalParameterName { get; init; }
    public double LogicalParameterValue { get; init; }
    public double TargetOriginalValue { get; init; }
    public long SegmentLocalTick { get; init; }
    public MappingStableIdV1 TrackId { get; init; }
    public MappingStableIdV1 SegmentId { get; init; }
    public MappingStableIdV1 SubVoiceId { get; init; }
    public string? SubVoiceName { get; init; }
    public int SubVoiceIndex { get; init; }
    public int SubVoiceEffectiveRootNote { get; init; }
    public MappingStableIdV1 EventInstrumentId { get; init; }
    public string? EventInstrumentName { get; init; }
    public int EventInstrumentRootNote { get; init; }
}
