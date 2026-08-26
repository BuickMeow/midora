using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyMappingChain(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceMappingChainId)
    {
        ArgumentNullException.ThrowIfNull(document);
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        MappingChain chain = ProjectDomainEditCommands.FindMappingChainForClipboard(
            instrument,
            sourceMappingChainId);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MappingChain,
            chain.Count,
            chain.Count == 1 ? "1 Mapping Step" : $"{chain.Count} Mapping Steps",
            new MappingChainClipboardData(eventInstrumentId, SnapshotMappingChain(chain)));
    }

    public static ProjectObjectClipboardPayload CopySubVoiceTimelineEvents(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(templateEventIds);
        if (templateEventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one SubVoice timeline event must be copied.",
                nameof(templateEventIds));
        }
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        SubVoice voice = instrument.SubVoices
            .SingleOrDefault(value => value.Id == sourceSubVoiceId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceSubVoiceId));
        HashSet<MidoraId> requested = ValidateDistinctIds(
            templateEventIds,
            nameof(templateEventIds));
        TemplateEvent[] selected = voice.Events
            .ResolveByIdsInCollectionOrder(requested)
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Id)
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Template Event must belong to the source SubVoice.",
                nameof(templateEventIds));
        }
        long earliest = selected[0].Tick;
        TemplateEventClipboardSnapshot[] snapshots = selected.Select(value =>
            SnapshotTemplateEvent(value, checked(value.Tick - earliest))).ToArray();
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.SubVoiceTimelineEvents,
            snapshots.Length,
            snapshots.Length == 1
                ? "1 SubVoice Timeline Event"
                : $"{snapshots.Length} SubVoice Timeline Events",
            new SubVoiceTimelineEventsClipboardData(eventInstrumentId, snapshots));
    }

    public static ProjectObjectClipboardPayload CopyValueCurveContent(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        MidoraId sourceCurveId,
        IReadOnlyCollection<MidoraId> pointIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pointIds);
        if (pointIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Value Curve point must be copied.",
                nameof(pointIds));
        }
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        SubVoice voice = instrument.SubVoices
            .SingleOrDefault(value => value.Id == sourceSubVoiceId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceSubVoiceId));
        ValueCurve curve = voice.Curves
            .SingleOrDefault(value => value.Id == sourceCurveId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceCurveId));
        HashSet<MidoraId> requested = ValidateDistinctIds(pointIds, nameof(pointIds));
        CurvePoint[] selected = curve.Points
            .ResolveByIdsInCollectionOrder(requested)
            .ToArray();
        if (selected.Length != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Value Curve point must belong to the source Curve.",
                nameof(pointIds));
        }
        CurvePointClipboardSnapshot[] points = SnapshotTimelinePoints(selected);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.ValueCurveContent,
            points.Length,
            points.Length == 1 ? "1 Value Curve Point" : $"{points.Length} Value Curve Points",
            new ValueCurveContentClipboardData(eventInstrumentId, curve.Target, points));
    }

    public static IProjectEditCommand CreatePasteSubVoiceTimelineEventsCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        long editCursorTick)
    {
        SubVoiceTimelineEventsClipboardData data =
            RequirePayload<SubVoiceTimelineEventsClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.SubVoiceTimelineEvents);
        return ProjectDomainEditCommands.PasteSubVoiceTimelineEventsClipboard(
            data,
            targetEventInstrumentId,
            targetSubVoiceId,
            editCursorTick);
    }

    public static IProjectEditCommand CreatePasteValueCurveContentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        MidoraId targetCurveId,
        long editCursorTick)
    {
        ValueCurveContentClipboardData data = RequirePayload<ValueCurveContentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.ValueCurveContent);
        return ProjectDomainEditCommands.PasteValueCurveContentClipboard(
            data,
            targetEventInstrumentId,
            targetSubVoiceId,
            targetCurveId,
            editCursorTick);
    }

    public static IProjectEditCommand CreatePasteMappingChainCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        bool nonEmptyReplacementConfirmed)
    {
        MappingChainClipboardData data = RequirePayload<MappingChainClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.MappingChain);
        return ProjectDomainEditCommands.PasteMappingChainClipboard(
            data,
            targetEventInstrumentId,
            targetMappingChainId,
            nonEmptyReplacementConfirmed);
    }

    private static TemplateEventClipboardSnapshot SnapshotTemplateEvent(
        TemplateEvent value,
        long tickOffset) =>
        new(
            value.Kind,
            tickOffset,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            value.FollowPitchDelta);

    private static MappingChainClipboardSnapshot SnapshotMappingChain(MappingChain chain) =>
        new(
            chain.IsEnabled,
            chain.Select(value => new MappingStepClipboardSnapshot(
                value.IsEnabled,
                value.Source,
                value.Operation,
                value.LogicalParameterId,
                value.EnvelopeId,
                value.MappingFunctionId,
                value.Constant,
                value.SourceMinimum,
                value.SourceMaximum,
                value.TargetMinimum,
                value.TargetMaximum,
                value.InputOverflow,
                value.DivideByZero)).ToArray());
}

internal sealed record SubVoiceTimelineEventsClipboardData(
    MidoraId SourceEventInstrumentId,
    TemplateEventClipboardSnapshot[] Events) : ProjectObjectClipboardData;

internal sealed record ValueCurveContentClipboardData(
    MidoraId SourceEventInstrumentId,
    MidiValueTarget Target,
    CurvePointClipboardSnapshot[] Points) : ProjectObjectClipboardData;

internal sealed record MappingChainClipboardData(
    MidoraId SourceEventInstrumentId,
    MappingChainClipboardSnapshot Chain) : ProjectObjectClipboardData;

internal sealed record TemplateEventClipboardSnapshot(
    TemplateEventKind Kind,
    long TickOffset,
    long LengthTicks,
    int Number,
    int Value,
    int SecondaryValue,
    bool HasBankMsb,
    bool HasBankLsb,
    bool FollowPitchDelta);

internal sealed record MappingChainClipboardSnapshot(
    bool IsEnabled,
    MappingStepClipboardSnapshot[] Steps);

internal sealed record MappingStepClipboardSnapshot(
    bool IsEnabled,
    MappingSource Source,
    MappingOperation Operation,
    MidoraId? LogicalParameterId,
    MidoraId? EnvelopeId,
    MidoraId? MappingFunctionId,
    double Constant,
    double SourceMinimum,
    double SourceMaximum,
    double TargetMinimum,
    double TargetMaximum,
    MappingInputOverflow InputOverflow,
    DivideByZeroPolicy DivideByZero);

internal readonly record struct IntegerTargetSettingsClipboardSnapshot(
    MappingRounding Rounding,
    MappingOverflow Overflow);

public static partial class ProjectDomainEditCommands
{
    internal static MappingChain FindMappingChainForClipboard(
        EventInstrument instrument,
        MidoraId mappingChainId) =>
        FindMappingChain(instrument, mappingChainId);

    internal static IProjectEditCommand PasteMappingChainClipboard(
        MappingChainClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        bool nonEmptyReplacementConfirmed) =>
        Command("Paste mapping chain", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Mapping Chains can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            MappingChain target = FindMappingChain(instrument, targetMappingChainId);
            ValidateMappingChainClipboard(snapshot.Chain);
            if (target.Count != 0 && !nonEmptyReplacementConfirmed)
            {
                throw new InvalidOperationException(
                    "Replacing a non-empty Mapping Chain requires explicit confirmation.");
            }
            MappingChain? replacement = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (replacement is null)
                    {
                        replacement = new MappingChain(owner);
                        ApplyMappingChainClipboard(owner, replacement, snapshot.Chain);
                    }
                    ReplaceMappingChain(instrument, target, replacement);
                },
                _ => ReplaceMappingChain(
                    instrument,
                    replacement ?? throw new InvalidOperationException(
                        "The pasted Mapping Chain does not exist before Apply."),
                    target));
        });

    internal static IProjectEditCommand PasteSubVoiceTimelineEventsClipboard(
        SubVoiceTimelineEventsClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        long editCursorTick) =>
        Command("Paste subvoice timeline events", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Events.Length == 0)
            {
                throw new ArgumentOutOfRangeException(
                    editCursorTick < 0 ? nameof(editCursorTick) : nameof(snapshot));
            }
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "SubVoice timeline events can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, targetSubVoiceId);
            TemplateEventClipboardValue[] values = snapshot.Events.Select(value =>
                PrepareTemplateEventClipboardValue(value, editCursorTick)).ToArray();
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(
                oldTemplateLength,
                values.Max(value => value.Value.Kind == TemplateEventKind.Note
                    ? checked(value.Value.Tick + value.Value.LengthTicks)
                    : checked(value.Value.Tick + 1)));
            TemplateEvent[]? copies = null;
            IPreparedProjectEdit prepared = Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    copies ??= values.Select(value =>
                        CreateTemplateEventClipboardCopy(owner, value)).ToArray();
                    voice.Events.AddRange(copies);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Pasted Template Events do not exist before Apply.");
                    }
                    int removed = voice.Events.RemoveRange(copies);
                    if (removed != copies.Length)
                        throw new InvalidOperationException("The pasted Template Event set is no longer present.");
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
            prepared = ResolveTargetedExactTemplateNoteCollisions(
                prepared,
                values
                    .Where(static value => value.Value.Kind == TemplateEventKind.Note)
                    .Select(value => new TemplateNoteCollisionTarget(
                        voice,
                        value.Value.Tick,
                        value.Value.Number)));
            return ResolveTargetedExactTemplateEventPointCollisions(
                prepared,
                values
                    .Where(static value => value.Value.Kind != TemplateEventKind.Note)
                    .SelectMany(value => CreateTemplateEventPointCollisionTargets(
                        voice,
                        value.Value)));
        });

    internal static IProjectEditCommand PasteValueCurveContentClipboard(
        ValueCurveContentClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        MidoraId targetCurveId,
        long editCursorTick) =>
        Command("Paste value curve points", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Points.Length == 0)
            {
                throw new ArgumentOutOfRangeException(
                    editCursorTick < 0 ? nameof(editCursorTick) : nameof(snapshot));
            }
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Value Curve content can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, targetSubVoiceId);
            ValueCurve curve = FindValueCurve(voice, targetCurveId);
            if (curve.Target != snapshot.Target)
            {
                throw new InvalidOperationException(
                    "Value Curve content requires an exact MIDI target.");
            }
            CurvePointClipboardValue[] values = snapshot.Points.Select(value =>
            {
                long tick = checked(editCursorTick + value.Tick);
                ValidateValueCurvePoint(
                    curve,
                    default,
                    tick,
                    value.Value,
                    value.Interpolation);
                return new CurvePointClipboardValue(tick, value.Value, value.Interpolation);
            }).ToArray();
            if (values.Select(value => value.Tick).Distinct().Count() != values.Length)
            {
                throw new InvalidOperationException(
                    "Pasted Value Curve points would create duplicate point ticks.");
            }
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(
                oldTemplateLength,
                checked(values.Max(value => value.Tick) + 1));
            CurvePoint[]? copies = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    copies ??= values.Select(value => new CurvePoint(
                        owner,
                        value.Tick,
                        value.Value,
                        value.Interpolation)).ToArray();
                    curve.Points.AddRange(copies);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Pasted Value Curve points do not exist before Apply.");
                    }
                    int removed = curve.Points.RemoveRange(copies);
                    if (removed != copies.Length)
                    {
                        throw new InvalidOperationException(
                            "The pasted Value Curve point set is no longer present.");
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
        });

    private static TemplateEventClipboardValue PrepareTemplateEventClipboardValue(
        TemplateEventClipboardSnapshot snapshot,
        long editCursorTick)
    {
        TemplateEventValue value = new(
            snapshot.Kind,
            checked(editCursorTick + snapshot.TickOffset),
            snapshot.LengthTicks,
            snapshot.Number,
            snapshot.Value,
            snapshot.SecondaryValue,
            snapshot.HasBankMsb,
            snapshot.HasBankLsb,
            snapshot.FollowPitchDelta);
        ValidateTemplateEventCreation(value);
        return new(value);
    }

    private static void ValidateMappingChainClipboard(MappingChainClipboardSnapshot chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        foreach (MappingStepClipboardSnapshot step in chain.Steps)
        {
            ValidateMappingStepValue(ToMappingStepValue(step));
        }
    }

    private static TemplateEvent CreateTemplateEventClipboardCopy(
        MidoraProject project,
        TemplateEventClipboardValue value)
    {
        TemplateEvent result = new(project);
        SetTemplateEvent(result, value.Value);
        return result;
    }

    private static void ApplyMappingChainClipboard(
        MidoraProject project,
        MappingChain target,
        MappingChainClipboardSnapshot snapshot)
    {
        target.IsEnabled = snapshot.IsEnabled;
        foreach (MappingStepClipboardSnapshot value in snapshot.Steps)
        {
            ValueMappingStep step = new(project) { IsEnabled = value.IsEnabled };
            SetMappingStep(step, ToMappingStepValue(value));
            target.Add(step);
        }
    }

    private static MappingStepValue ToMappingStepValue(MappingStepClipboardSnapshot value) =>
        new(
            value.Source,
            value.Operation,
            value.LogicalParameterId,
            value.EnvelopeId,
            value.MappingFunctionId,
            value.Constant,
            value.SourceMinimum,
            value.SourceMaximum,
            value.TargetMinimum,
            value.TargetMaximum,
            value.InputOverflow,
            value.DivideByZero);

    private sealed record TemplateEventClipboardValue(TemplateEventValue Value);
}
