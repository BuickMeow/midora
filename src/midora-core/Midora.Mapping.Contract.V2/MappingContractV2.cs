using System.Globalization;

namespace Midora.Mapping.Contract.V2;

public static class MappingAbiV2
{
    public const int Version = 2;
    public const string NetCoreReferencePackVersion = "10.0.10";
    public const string CompilerProfileId = "midora-csharp14-net10.0.10-roslyn5.3-v2";
}

public readonly record struct MappingStableIdV2
{
    public MappingStableIdV2(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Mapping stable IDs cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }
    public bool IsEmpty => Value == 0;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public enum MappingTargetParameterV2
{
    Unknown = 0,
    Number = 1,
    Value = 2,
    SecondaryValue = 3,
    LogicalParameterOutput = 4
}

public enum MappingEventKindV2
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

public readonly record struct MappingContextV2(
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
    public MappingStableIdV2 CurrentEventId { get; init; }
    public MappingTargetParameterV2 CurrentParameter { get; init; }
    public MappingEventKindV2 CurrentEventKind { get; init; }
    public MappingStableIdV2 LogicalParameterId { get; init; }
    public string? LogicalParameterName { get; init; }
    public double LogicalParameterValue { get; init; }
    public double TargetOriginalValue { get; init; }
    public long SegmentLocalTick { get; init; }
    public MappingStableIdV2 TrackId { get; init; }
    public MappingStableIdV2 SegmentId { get; init; }
    public MappingStableIdV2 SubVoiceId { get; init; }
    public string? SubVoiceName { get; init; }
    public int SubVoiceIndex { get; init; }
    public int SubVoiceEffectiveRootNote { get; init; }
    public MappingStableIdV2 EventInstrumentId { get; init; }
    public string? EventInstrumentName { get; init; }
    public int EventInstrumentRootNote { get; init; }
}
