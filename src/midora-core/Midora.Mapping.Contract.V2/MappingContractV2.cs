using System.Globalization;
using System.Collections.Frozen;

namespace Midora.Mapping.Contract.V2;

public static class MappingAbiV2
{
    public const int Version = 2;
    public const string NetCoreReferencePackVersion = "10.0.10";
    public const string CompilerProfileId = "midora-csharp14-net10.0.10-roslyn5.3-v2";
}

public static class MappingExpressionAbiV3
{
    public const int Version = 3;
    public const string CompilerProfileId = "midora-bounded-mapping-expression-v3";
    public const int MaximumSourceLength = 8192;
    public const int MaximumSyntaxNodes = 512;
    public const int MaximumSyntaxDepth = 64;
}

public static class MappingExpressionLanguageV3
{
    public static IReadOnlySet<string> NumericContextFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MappingContextV2.CurrentValue),
        nameof(MappingContextV2.TriggerNote),
        nameof(MappingContextV2.TriggerVelocity),
        nameof(MappingContextV2.GateLength),
        nameof(MappingContextV2.PitchDelta),
        nameof(MappingContextV2.TemplateTick),
        nameof(MappingContextV2.ProjectTick),
        nameof(MappingContextV2.TemplateNote),
        nameof(MappingContextV2.TemplateVelocity),
        nameof(MappingContextV2.EffectiveRootNote),
        nameof(MappingContextV2.LogicalParameterValue),
        nameof(MappingContextV2.TargetOriginalValue),
        nameof(MappingContextV2.SegmentLocalTick),
        nameof(MappingContextV2.SubVoiceIndex),
        nameof(MappingContextV2.SubVoiceEffectiveRootNote),
        nameof(MappingContextV2.EventInstrumentRootNote)
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> EnumContextFields { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(MappingContextV2.CurrentParameter),
        nameof(MappingContextV2.CurrentEventKind)
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> MathMethodNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Math.Abs), nameof(Math.Acos), nameof(Math.Acosh), nameof(Math.Asin),
        nameof(Math.Asinh), nameof(Math.Atan), nameof(Math.Atan2), nameof(Math.Atanh),
        nameof(Math.Cbrt), nameof(Math.Ceiling), nameof(Math.Clamp), nameof(Math.CopySign),
        nameof(Math.Cos), nameof(Math.Cosh), nameof(Math.Exp), nameof(Math.Floor),
        nameof(Math.IEEERemainder), nameof(Math.Log), nameof(Math.Log10), nameof(Math.Log2),
        nameof(Math.Max), nameof(Math.MaxMagnitude), nameof(Math.Min), nameof(Math.MinMagnitude),
        nameof(Math.Pow), nameof(Math.Round), nameof(Math.Sin), nameof(Math.Sinh),
        nameof(Math.Sqrt), nameof(Math.Tan), nameof(Math.Tanh), nameof(Math.Truncate)
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> MathConstantNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Math.E), nameof(Math.PI), nameof(Math.Tau)
    }.ToFrozenSet(StringComparer.Ordinal);
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
