using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Desktop;

internal sealed record MappingChainEditingContext(
    MappingChain Chain,
    LogicalParameterMapping? ParameterMapping,
    SubVoice? SubVoice,
    SubVoiceEventMapping? EventMapping)
{
    public bool IsLogicalParameterMapping => ParameterMapping is not null;
    public bool IsNoteEventMapping => EventMapping?.Target.EventKind == TemplateEventKind.Note;
    public bool IsNoteNumberMapping => IsNoteEventMapping
        && EventMapping!.Target.Parameter == TemplateEventMappingParameter.Number;
    public bool IsNoteVelocityMapping => IsNoteEventMapping
        && EventMapping!.Target.Parameter == TemplateEventMappingParameter.Value;
    public MidiIntegerTargetSettings TargetSettings =>
        ParameterMapping?.TargetSettings
        ?? EventMapping?.TargetSettings
        ?? throw new InvalidOperationException("The Mapping Chain has no target settings.");
}

internal static class MappingEditingPolicy
{
    public static MappingChainEditingContext Resolve(
        EventInstrument instrument,
        MidoraId chainId)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        LogicalParameterMapping[] parameterMappings = instrument.ParameterMappings
            .Where(value => value.Steps.Id == chainId)
            .ToArray();
        (SubVoice Voice, SubVoiceEventMapping Mapping)[] eventMappings = instrument.SubVoices
            .SelectMany(voice => voice.EventMappings
                .Where(value => value.Steps.Id == chainId)
                .Select(value => (Voice: voice, Mapping: value)))
            .ToArray();
        return (parameterMappings.Length + eventMappings.Length) switch
        {
            1 when parameterMappings.Length == 1 =>
                new(parameterMappings[0].Steps, parameterMappings[0], null, null),
            1 => new(
                eventMappings[0].Mapping.Steps,
                null,
                eventMappings[0].Voice,
                eventMappings[0].Mapping),
            0 => throw new ArgumentOutOfRangeException(nameof(chainId)),
            _ => throw new InvalidOperationException(
                "This Event Instrument contains a duplicate Mapping Chain.")
        };
    }

    public static IReadOnlyList<MappingSource> AllowedSources(
        EventInstrument instrument,
        MappingChainEditingContext context)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(context);
        return Enum.GetValues<MappingSource>()
            .Where(source => IsSourceAllowed(instrument, context, source))
            .ToArray();
    }

    public static IReadOnlyList<CSharpMappingFunction> AllowedFunctions(
        EventInstrument instrument,
        MappingChainEditingContext context)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(context);
        if (!instrument.RequiresChannelIsolation && context.IsNoteNumberMapping)
        {
            return [];
        }
        return instrument.MappingFunctions
            .Where(function => IsFunctionAllowed(instrument, context, function))
            .ToArray();
    }

    public static string DescribeSource(MappingSource source) => source switch
    {
        MappingSource.CurrentValue => "Value produced by the preceding Mapping Step",
        MappingSource.TriggerNote => "Logical Note pitch for this instance",
        MappingSource.TriggerVelocity => "Logical Note velocity for this instance",
        MappingSource.GateLength => "Logical Note gate length",
        MappingSource.PitchDelta => "Logical Note pitch minus effective root note",
        MappingSource.TemplateTick => "Tick inside the Event Instrument template",
        MappingSource.ProjectTick => "Absolute Project tick",
        MappingSource.TemplateNote => "Original pitch of the current Template Note",
        MappingSource.TemplateVelocity => "Original velocity of the current Template Note",
        MappingSource.LogicalParameter => "A Logical Parameter selected below",
        MappingSource.Envelope => "An Envelope Preset selected below",
        MappingSource.Constant => "The Constant field",
        _ => source.ToString()
    };

    private static bool IsSourceAllowed(
        EventInstrument instrument,
        MappingChainEditingContext context,
        MappingSource source)
    {
        if (source == MappingSource.LogicalParameter
            && instrument.LogicalParameters.Count == 0)
        {
            return false;
        }
        if (source == MappingSource.Envelope && instrument.Envelopes.Count == 0)
        {
            return false;
        }
        if (source is MappingSource.TemplateNote or MappingSource.TemplateVelocity
            && !context.IsNoteEventMapping)
        {
            return false;
        }
        if (instrument.RequiresChannelIsolation)
        {
            return true;
        }
        if (context.IsNoteNumberMapping)
        {
            return false;
        }
        if (source == MappingSource.Envelope)
        {
            return false;
        }
        if (source == MappingSource.TriggerVelocity)
        {
            return context.IsNoteVelocityMapping;
        }
        return source is not (
            MappingSource.TriggerNote
            or MappingSource.GateLength
            or MappingSource.PitchDelta);
    }

    private static bool IsFunctionAllowed(
        EventInstrument instrument,
        MappingChainEditingContext context,
        CSharpMappingFunction function)
    {
        if (!context.IsNoteEventMapping
            && (function.DeclaredContextFields.Contains(nameof(MappingContextV2.TemplateNote))
                || function.DeclaredContextFields.Contains(nameof(MappingContextV2.TemplateVelocity))))
        {
            return false;
        }
        if (instrument.RequiresChannelIsolation)
        {
            return true;
        }
        return !function.DeclaredContextFields.Any(IsPerNoteContextField);
    }

    private static bool IsPerNoteContextField(string field) => field is
        nameof(MappingContextV2.TriggerNote)
        or nameof(MappingContextV2.TriggerVelocity)
        or nameof(MappingContextV2.EffectiveRootNote)
        or nameof(MappingContextV2.PitchDelta)
        or nameof(MappingContextV2.GateLength)
        or nameof(MappingContextV2.SegmentLocalTick);
}
