using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateTempo(
        MidoraId tempoId,
        long tick,
        decimal beatsPerMinute) =>
        Command("Change tempo", project =>
        {
            int index = FindIndex(project.Conductor.Tempos, tempoId, value => value.Id, nameof(tempoId));
            TempoChange current = project.Conductor.Tempos[index];
            ValidateConductorTick(tick, nameof(tick));
            ValidateTempo(beatsPerMinute);
            EnsureUniqueTick(project.Conductor.Tempos, tempoId, tick, value => value.Id, value => value.Tick);
            if (current.Tick == 0 && tick != 0)
            {
                throw new InvalidOperationException("The tick 0 Tempo cannot be moved.");
            }
            TempoChange replacement = new(current.Id, tick, beatsPerMinute);
            return Prepared(
                current != replacement,
                ConductorChange(),
                value => ReplaceRequired(value.Conductor.Tempos, current, replacement, "Tempo"),
                value => ReplaceRequired(value.Conductor.Tempos, replacement, current, "Tempo"));
        });

    public static IProjectEditCommand DeleteTempo(MidoraId tempoId) =>
        Command("Delete tempo", project =>
        {
            int index = FindIndex(project.Conductor.Tempos, tempoId, value => value.Id, nameof(tempoId));
            TempoChange tempo = project.Conductor.Tempos[index];
            if (tempo.Tick == 0)
            {
                throw new InvalidOperationException("The tick 0 Tempo cannot be deleted.");
            }
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                value => RemoveRequired(value.Conductor.Tempos, tempo, "Tempo"),
                value => InsertAt(value.Conductor.Tempos, index, tempo, "Tempo"));
        });

    public static IProjectEditCommand UpdateTimeSignature(
        MidoraId timeSignatureId,
        long tick,
        int numerator,
        int denominator) =>
        Command("Change time signature", project =>
        {
            int index = FindIndex(
                project.Conductor.TimeSignatures,
                timeSignatureId,
                value => value.Id,
                nameof(timeSignatureId));
            TimeSignatureChange current = project.Conductor.TimeSignatures[index];
            ValidateConductorTick(tick, nameof(tick));
            if (numerator is < 1 or > 99)
            {
                throw new ArgumentOutOfRangeException(nameof(numerator));
            }
            if (denominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
            {
                throw new ArgumentOutOfRangeException(nameof(denominator));
            }
            ProjectTimeSignatureRules.ValidateCompatibility(
                project.TicksPerQuarterNote,
                denominator,
                nameof(denominator));
            EnsureUniqueTick(
                project.Conductor.TimeSignatures,
                timeSignatureId,
                tick,
                value => value.Id,
                value => value.Tick);
            if (current.Tick == 0 && tick != 0)
            {
                throw new InvalidOperationException("The tick 0 Time Signature cannot be moved.");
            }
            TimeSignatureChange replacement = new(current.Id, tick, numerator, denominator);
            return Prepared(
                current != replacement,
                ConductorChange(),
                value => ReplaceRequired(
                    value.Conductor.TimeSignatures,
                    current,
                    replacement,
                    "Time Signature"),
                value => ReplaceRequired(
                    value.Conductor.TimeSignatures,
                    replacement,
                    current,
                    "Time Signature"));
        });

    public static IProjectEditCommand DeleteTimeSignature(MidoraId timeSignatureId) =>
        Command("Delete time signature", project =>
        {
            int index = FindIndex(
                project.Conductor.TimeSignatures,
                timeSignatureId,
                value => value.Id,
                nameof(timeSignatureId));
            TimeSignatureChange signature = project.Conductor.TimeSignatures[index];
            if (signature.Tick == 0)
            {
                throw new InvalidOperationException(
                    "The tick 0 Time Signature cannot be deleted.");
            }
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                value => RemoveRequired(
                    value.Conductor.TimeSignatures,
                    signature,
                    "Time Signature"),
                value => InsertAt(
                    value.Conductor.TimeSignatures,
                    index,
                    signature,
                    "Time Signature"));
        });

    public static IProjectEditCommand UpdateKeySignature(
        MidoraId keySignatureId,
        long tick,
        int sharpsFlats,
        bool isMinor) =>
        Command("Change key signature", project =>
        {
            int index = FindIndex(
                project.Conductor.KeySignatures,
                keySignatureId,
                value => value.Id,
                nameof(keySignatureId));
            KeySignatureChange current = project.Conductor.KeySignatures[index];
            ValidateConductorTick(tick, nameof(tick));
            if (sharpsFlats is < -7 or > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(sharpsFlats));
            }
            EnsureUniqueTick(
                project.Conductor.KeySignatures,
                keySignatureId,
                tick,
                value => value.Id,
                value => value.Tick);
            KeySignatureChange replacement = new(current.Id, tick, sharpsFlats, isMinor);
            return Prepared(
                current != replacement,
                ConductorChange(),
                value => ReplaceRequired(
                    value.Conductor.KeySignatures,
                    current,
                    replacement,
                    "Key Signature"),
                value => ReplaceRequired(
                    value.Conductor.KeySignatures,
                    replacement,
                    current,
                    "Key Signature"));
        });

    public static IProjectEditCommand DeleteKeySignature(MidoraId keySignatureId) =>
        Command("Delete key signature", project =>
        {
            int index = FindIndex(
                project.Conductor.KeySignatures,
                keySignatureId,
                value => value.Id,
                nameof(keySignatureId));
            KeySignatureChange signature = project.Conductor.KeySignatures[index];
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                value => RemoveRequired(
                    value.Conductor.KeySignatures,
                    signature,
                    "Key Signature"),
                value => InsertAt(
                    value.Conductor.KeySignatures,
                    index,
                    signature,
                    "Key Signature"));
        });

    public static IProjectEditCommand UpdateProjectMarker(
        MidoraId markerId,
        long tick,
        string name) =>
        Command("Change project marker", project =>
        {
            int index = FindIndex(project.Conductor.Markers, markerId, value => value.Id, nameof(markerId));
            ProjectMarker current = project.Conductor.Markers[index];
            ValidateConductorTick(tick, nameof(tick));
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            ProjectMarker replacement = new(current.Id, tick, normalized);
            return Prepared(
                current != replacement,
                ConductorChange(),
                value => ReplaceRequired(
                    value.Conductor.Markers,
                    current,
                    replacement,
                    "Project Marker"),
                value => ReplaceRequired(
                    value.Conductor.Markers,
                    replacement,
                    current,
                    "Project Marker"));
        });

    public static IProjectEditCommand DeleteProjectMarker(MidoraId markerId) =>
        Command("Delete project marker", project =>
        {
            int index = FindIndex(project.Conductor.Markers, markerId, value => value.Id, nameof(markerId));
            ProjectMarker marker = project.Conductor.Markers[index];
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                value => RemoveRequired(value.Conductor.Markers, marker, "Project Marker"),
                value => InsertAt(value.Conductor.Markers, index, marker, "Project Marker"));
        });

    public static IProjectEditCommand UpdateProjectEndMarker(long tick) =>
        Command("Move project end marker", project =>
        {
            ProjectEndMarker marker = project.Conductor.EndMarker
                ?? throw new InvalidOperationException("The Project End Marker does not exist.");
            ValidateConductorTick(tick, nameof(tick));
            long oldTick = marker.Tick;
            return Prepared(
                oldTick != tick,
                ConductorChange(),
                _ => marker.Tick = tick,
                _ => marker.Tick = oldTick);
        });

    public static IProjectEditCommand DeleteProjectEndMarker() =>
        Command("Delete project end marker", project =>
        {
            ProjectEndMarker marker = project.Conductor.EndMarker
                ?? throw new InvalidOperationException("The Project End Marker does not exist.");
            return Prepared(
                hasChanges: true,
                ConductorChange(),
                value =>
                {
                    if (!ReferenceEquals(value.Conductor.EndMarker, marker))
                    {
                        throw new InvalidOperationException(
                            "The Project End Marker is no longer present.");
                    }
                    value.Conductor.EndMarker = null;
                },
                value =>
                {
                    if (value.Conductor.EndMarker is not null)
                    {
                        throw new InvalidOperationException(
                            "A Project End Marker is already present.");
                    }
                    value.Conductor.EndMarker = marker;
                });
        });

    private static int FindIndex<T>(
        List<T> values,
        MidoraId id,
        Func<T, MidoraId> getId,
        string parameterName)
    {
        int result = -1;
        for (int index = 0; index < values.Count; index++)
        {
            if (getId(values[index]) != id)
            {
                continue;
            }
            if (result >= 0)
            {
                throw new InvalidOperationException("The Project stable ID is duplicated.");
            }
            result = index;
        }
        return result >= 0 ? result : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void EnsureUniqueTick<T>(
        IEnumerable<T> values,
        MidoraId excludedId,
        long tick,
        Func<T, MidoraId> getId,
        Func<T, long> getTick)
    {
        if (values.Any(value => getId(value) != excludedId && getTick(value) == tick))
        {
            throw new InvalidOperationException(
                "Only one Conductor event of this type is allowed at a tick.");
        }
    }

    private static void ValidateConductorTick(long tick, string parameterName)
    {
        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateTempo(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute));
        }
        decimal exact;
        try
        {
            exact = 60_000_000m / beatsPerMinute;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute),
                beatsPerMinute,
                "Tempo cannot be represented by the MIDI 1.0 Set Tempo field.");
        }
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        if (rounded is < 1m or > 16_777_215m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(beatsPerMinute),
                beatsPerMinute,
                "Tempo cannot be represented by the MIDI 1.0 Set Tempo field.");
        }
    }

    private static void ReplaceRequired<T>(
        List<T> values,
        T expected,
        T replacement,
        string objectName)
        where T : class
    {
        int index = values.IndexOf(expected);
        if (index < 0)
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
        values[index] = replacement;
    }
}
