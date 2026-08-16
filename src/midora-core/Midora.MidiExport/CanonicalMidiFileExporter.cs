using System.Numerics;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport;

public static class CanonicalMidiFileExporter
{
    private const string GenericEncodingErrorCode = "MIDORA-MIDI-EXPORT-ENCODING";
    private static ReadOnlySpan<byte> RolandGsChannel10NormalPart =>
        [0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1b, 0xf7];
    private static ReadOnlySpan<byte> YamahaXgChannel10NormalPart =>
        [0x43, 0x10, 0x4c, 0x08, 0x09, 0x07, 0x00, 0xf7];

    public static MidiExportEncodingResult EncodeWholeProject(WholeProjectMidiEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTracks);

        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            request.LogicalTracks,
            includedTrackId: null,
            includedPort: null,
            static port => port,
            requireIncludedEvent: false);
    }

    public static MidiExportEncodingResult EncodeLogicalTrack(LogicalTrackMidiEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTrack);

        MidoraId trackId = request.LogicalTrack.TrackId;
        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            [request.LogicalTrack],
            trackId,
            includedPort: null,
            static port => port,
            requireIncludedEvent: false);
    }

    public static MidiExportEncodingResult EncodePort(PortMidiEncodingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CompiledResult);
        ArgumentNullException.ThrowIfNull(request.ConductorTrackName);
        ArgumentNullException.ThrowIfNull(request.LogicalTracks);
        if (request.ZeroBasedOriginalPort > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ZeroBasedOriginalPort));
        }

        byte selectedPort = request.ZeroBasedOriginalPort;
        return EncodeCore(
            request.CompiledResult,
            request.ConductorTrackName,
            request.LogicalTracks,
            includedTrackId: null,
            includedPort: selectedPort,
            static _ => 0,
            requireIncludedEvent: true);
    }

    private static MidiExportEncodingResult EncodeCore(
        CanonicalCompiledResult compiledResult,
        string conductorTrackName,
        IReadOnlyList<MidiExportLogicalTrackLayout> logicalTracks,
        MidoraId? includedTrackId,
        byte? includedPort,
        Func<byte, byte> mapOutputPort,
        bool requireIncludedEvent)
    {
        List<MidiExportDiagnostic> diagnostics = [];
        try
        {
            ValidateCompiledResult(compiledResult, diagnostics);
            Dictionary<MidoraId, MidiExportLogicalTrackLayout> layouts =
                BuildLayoutIndex(logicalTracks, diagnostics);
            Dictionary<MidiExportChannelUnit, string> unitTrackNames =
                BuildUnitTrackNameIndex(logicalTracks, diagnostics);
            if (diagnostics.Count != 0)
            {
                return Failure(diagnostics);
            }

            long duration = checked(compiledResult.EndTick - compiledResult.StartTick);
            List<StandardMidiFileTrack> tracks =
                [BuildConductorTrack(compiledResult, conductorTrackName, duration)];

            Dictionary<MidiExportChannelUnit, List<CanonicalMidiEvent>> grouped = [];
            foreach (CanonicalMidiEvent value in compiledResult.Events)
            {
                ValidateCanonicalEvent(compiledResult, value, diagnostics);
                MidoraId ownerTrackId = ResolveTrackId(compiledResult, value, diagnostics);
                if (ownerTrackId == default)
                {
                    continue;
                }
                if (includedTrackId.HasValue && ownerTrackId != includedTrackId.Value)
                {
                    continue;
                }
                if (includedPort.HasValue && value.ZeroBasedPort != includedPort.Value)
                {
                    continue;
                }
                if (!layouts.TryGetValue(ownerTrackId, out MidiExportLogicalTrackLayout? ownerLayout))
                {
                    diagnostics.Add(new(
                        "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                        MidiExportDiagnosticCategory.CanonicalConsistency,
                        $"Canonical event references Track {ownerTrackId}, but the export layout does not contain it.",
                        value.Source));
                    continue;
                }

                MidiExportChannelUnit unit = new(value.ZeroBasedPort, value.ZeroBasedChannel);
                if (!ownerLayout.EventTrackNamesByUnit.ContainsKey(unit))
                {
                    diagnostics.Add(new(
                        "MIDORA-MIDI-EXPORT-UNIT-LAYOUT",
                        MidiExportDiagnosticCategory.Encoding,
                        $"The frozen layout for Track {ownerTrackId} does not contain Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1}.",
                        value.Source));
                    continue;
                }
                if (!unitTrackNames.ContainsKey(unit))
                {
                    diagnostics.Add(new(
                        "MIDORA-MIDI-EXPORT-UNIT-LAYOUT",
                        MidiExportDiagnosticCategory.Encoding,
                        $"The export layout does not provide a Track Name for Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1}.",
                        value.Source));
                    continue;
                }
                grouped.GetOrAdd(unit).Add(value);
            }
            if (diagnostics.Count != 0)
            {
                return Failure(diagnostics);
            }
            if (requireIncludedEvent && grouped.Count == 0)
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-UNUSED-PORT",
                    MidiExportDiagnosticCategory.Encoding,
                    "Per-Port export does not create a file for a Port with no canonical events."));
                return Failure(diagnostics);
            }

            foreach ((MidiExportChannelUnit unit, List<CanonicalMidiEvent> values) in grouped
                .OrderBy(group => group.Key.ZeroBasedPort)
                .ThenBy(group => group.Key.ZeroBasedChannel))
            {
                string eventTrackName = unitTrackNames[unit];
                ValidateUnitTrack(unit, values, diagnostics);
                ValidateBankProgramOrder(values, diagnostics);
                tracks.Add(BuildEventTrack(
                    compiledResult.StartTick,
                    duration,
                    unit,
                    mapOutputPort(unit.ZeroBasedPort),
                    eventTrackName,
                    values));
            }
            if (diagnostics.Count != 0)
            {
                return Failure(diagnostics);
            }

            byte[] bytes = StandardMidiFile.EncodeType1(
                compiledResult.TicksPerQuarterNote,
                tracks);
            return new(bytes, []);
        }
        catch (Exception exception) when (exception is MidoraMidiException
            or ArgumentException
            or OverflowException
            or System.Text.EncoderFallbackException)
        {
            diagnostics.Add(new(
                GenericEncodingErrorCode,
                MidiExportDiagnosticCategory.Encoding,
                exception.Message));
            return Failure(diagnostics);
        }
    }

    internal static int ConvertTempoToMicrosecondsPerQuarterNote(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            throw new MidoraMidiException("Tempo BPM must be greater than zero.");
        }
        decimal exact = 60_000_000m / beatsPerMinute;
        return RoundTempoMicrosecondsPerQuarterNote(exact);
    }

    internal static int RoundTempoMicrosecondsPerQuarterNote(decimal exact)
    {
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        if (rounded is < 1m or > 16_777_215m)
        {
            throw new MidoraMidiException(
                $"Set Tempo value {exact} cannot be represented by the 24-bit SMF field.");
        }
        return decimal.ToInt32(rounded);
    }

    private static void ValidateCompiledResult(
        CanonicalCompiledResult compiled,
        List<MidiExportDiagnostic> diagnostics)
    {
        if (compiled.Purpose != CompilationPurpose.MidiExport)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-CONTEXT",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "MIDI export only accepts a dedicated MidiExport CompileContext result."));
        }
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-NOT-CONSUMABLE",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "MIDI export cannot consume a failed, partial, or otherwise non-consumable canonical result."));
        }
        if (compiled.StartTick < 0 || compiled.EndTick < compiled.StartTick)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-RANGE",
                MidiExportDiagnosticCategory.CanonicalConsistency,
                "The canonical export range is invalid."));
        }
    }

    private static Dictionary<MidoraId, MidiExportLogicalTrackLayout> BuildLayoutIndex(
        IReadOnlyList<MidiExportLogicalTrackLayout> source,
        List<MidiExportDiagnostic> diagnostics)
    {
        Dictionary<MidoraId, MidiExportLogicalTrackLayout> result = [];
        foreach (MidiExportLogicalTrackLayout? value in source)
        {
            if (value is null)
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                    MidiExportDiagnosticCategory.Encoding,
                    "The MIDI export Track layout cannot contain null."));
                continue;
            }
            if (!result.TryAdd(value.TrackId, value))
            {
                diagnostics.Add(new(
                    "MIDORA-MIDI-EXPORT-TRACK-LAYOUT",
                    MidiExportDiagnosticCategory.Encoding,
                    $"The MIDI export Track layout contains duplicate Track ID {value.TrackId}."));
            }
        }
        return result;
    }

    private static Dictionary<MidiExportChannelUnit, string> BuildUnitTrackNameIndex(
        IReadOnlyList<MidiExportLogicalTrackLayout> source,
        List<MidiExportDiagnostic> diagnostics)
    {
        Dictionary<MidiExportChannelUnit, string> result = [];
        foreach (MidiExportLogicalTrackLayout? layout in source)
        {
            if (layout is null)
            {
                continue;
            }
            foreach ((MidiExportChannelUnit unit, string trackName) in layout.EventTrackNamesByUnit)
            {
                if (result.TryGetValue(unit, out string? existingTrackName))
                {
                    if (!string.Equals(existingTrackName, trackName, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new(
                            "MIDORA-MIDI-EXPORT-UNIT-LAYOUT",
                            MidiExportDiagnosticCategory.Encoding,
                            $"Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1} has conflicting MIDI Track Names in the frozen layout."));
                    }
                    continue;
                }
                result.Add(unit, trackName);
            }
        }
        return result;
    }

    private static StandardMidiFileTrack BuildConductorTrack(
        CanonicalCompiledResult compiled,
        string trackName,
        long duration)
    {
        List<(long Tick, int KindOrder, MidoraId StableId, StandardMidiFileEvent Event)> timed = [];
        foreach (CanonicalTempo value in compiled.Conductor.Tempos)
        {
            int microseconds = ConvertTempoToMicrosecondsPerQuarterNote(value.BeatsPerMinute);
            timed.Add((
                RelativeTick(compiled, value.Tick),
                0,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.SetTempoMetaType,
                    [
                        (byte)((microseconds >> 16) & 0xff),
                        (byte)((microseconds >> 8) & 0xff),
                        (byte)(microseconds & 0xff)
                    ])));
        }
        foreach (CanonicalTimeSignature value in compiled.Conductor.TimeSignatures)
        {
            if (value.Numerator is < 1 or > byte.MaxValue
                || value.Denominator <= 0
                || (value.Denominator & (value.Denominator - 1)) != 0)
            {
                throw new MidoraMidiException(
                    $"Time Signature {value.Numerator}/{value.Denominator} cannot be encoded in SMF.");
            }
            int exponent = BitOperations.TrailingZeroCount(checked((uint)value.Denominator));
            timed.Add((
                RelativeTick(compiled, value.Tick),
                1,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.TimeSignatureMetaType,
                    [checked((byte)value.Numerator), checked((byte)exponent), 24, 8])));
        }
        foreach (CanonicalKeySignature value in compiled.Conductor.KeySignatures)
        {
            if (value.SharpsFlats is < -7 or > 7)
            {
                throw new MidoraMidiException(
                    $"Key Signature sf value {value.SharpsFlats} is outside the SMF range -7..7.");
            }
            timed.Add((
                RelativeTick(compiled, value.Tick),
                2,
                value.SourceId,
                StandardMidiFileEvent.Meta(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.KeySignatureMetaType,
                    [unchecked((byte)(sbyte)value.SharpsFlats), value.IsMinor ? (byte)1 : (byte)0])));
        }
        foreach (CanonicalMarker value in compiled.Conductor.Markers)
        {
            timed.Add((
                RelativeTick(compiled, value.Tick),
                3,
                value.Id,
                StandardMidiFileEvent.Text(
                    RelativeTick(compiled, value.Tick),
                    StandardMidiFile.MarkerMetaType,
                    value.Name)));
        }

        List<StandardMidiFileEvent> events =
            [StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, trackName)];
        events.AddRange(timed
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.KindOrder)
            .ThenBy(value => value.StableId)
            .Select(value => value.Event));
        return new(duration, events);
    }

    private static StandardMidiFileTrack BuildEventTrack(
        long startTick,
        long duration,
        MidiExportChannelUnit unit,
        byte port,
        string trackName,
        IReadOnlyList<CanonicalMidiEvent> values)
    {
        List<StandardMidiFileEvent> events =
        [
            StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, trackName),
            StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [port])
        ];
        if (unit.ZeroBasedChannel == 9)
        {
            events.Add(StandardMidiFileEvent.SystemExclusive(0, RolandGsChannel10NormalPart));
            events.Add(StandardMidiFileEvent.SystemExclusive(0, YamahaXgChannel10NormalPart));
        }
        foreach (CanonicalMidiEvent value in values)
        {
            events.Add(StandardMidiFileEvent.ChannelVoice(
                checked(value.Tick - startTick),
                value.Message));
        }
        return new(duration, events);
    }

    private static void ValidateUnitTrack(
        MidiExportChannelUnit unit,
        IReadOnlyList<CanonicalMidiEvent> values,
        List<MidiExportDiagnostic> diagnostics)
    {
        foreach (CanonicalMidiEvent value in values)
        {
            if (value.ZeroBasedPort == unit.ZeroBasedPort
                && value.ZeroBasedChannel == unit.ZeroBasedChannel)
            {
                continue;
            }

            diagnostics.Add(new(
                "MIDORA-MIDI-EXPORT-UNIT-TRACK",
                MidiExportDiagnosticCategory.Encoding,
                $"A MIDI event Track for Port {unit.ZeroBasedPort + 1}, Channel {unit.ZeroBasedChannel + 1} contains an event from another Channel Unit.",
                value.Source));
        }
    }

    private static void ValidateCanonicalEvent(
        CanonicalCompiledResult compiled,
        CanonicalMidiEvent value,
        List<MidiExportDiagnostic> diagnostics)
    {
        MidiMessage message = value.Message;
        if (value.Tick < compiled.StartTick || value.Tick > compiled.EndTick)
        {
            Add("Canonical MIDI event lies outside the export range.");
        }
        if (value.ZeroBasedPort > 15 || value.ZeroBasedChannel > 15
            || !message.IsChannelVoiceMessage
            || message.ChannelNumber != value.ZeroBasedChannel)
        {
            Add("Canonical MIDI routing or status/channel data is inconsistent.");
            return;
        }
        if (message.MessageType == MidiMessageType.NoteOff && message.Byte2 != 0)
        {
            Add("Canonical Note Off must use velocity 0 for MIDI export.");
        }
        if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
        {
            Add("Canonical Note On velocity 0 is not an exportable Midora Note On.");
        }
        if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 is 91 or 93)
        {
            Add($"Unsupported CC{message.Byte1} reached the MIDI exporter.");
        }
        if (message.MessageType is not MidiMessageType.NoteOff
            and not MidiMessageType.NoteOn
            and not MidiMessageType.ControlChange
            and not MidiMessageType.ProgramChange
            and not MidiMessageType.PitchWheelChange)
        {
            Add($"Unknown or unsupported canonical MIDI event type {message.MessageType}.");
        }

        void Add(string messageText) => diagnostics.Add(new(
            "MIDORA-MIDI-EXPORT-CANONICAL",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            messageText,
            value.Source));
    }

    private static MidoraId ResolveTrackId(
        CanonicalCompiledResult compiled,
        CanonicalMidiEvent value,
        List<MidiExportDiagnostic> diagnostics)
    {
        if (value.Source.TrackId != default)
        {
            return value.Source.TrackId;
        }

        MidoraId[] candidates = compiled.Allocations
            .ToArray()
            .Where(allocation => allocation.ZeroBasedPort == value.ZeroBasedPort
                && allocation.ZeroBasedChannel == value.ZeroBasedChannel
                && (value.Tick == compiled.EndTick
                    ? allocation.StartTick < value.Tick && allocation.EndTick >= value.Tick
                    : allocation.StartTick <= value.Tick && allocation.EndTick > value.Tick))
            .OrderByDescending(allocation => allocation.StartTick)
            .Select(allocation => allocation.TrackId)
            .Distinct()
            .ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        diagnostics.Add(new(
            "MIDORA-MIDI-EXPORT-OWNER",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            candidates.Length == 0
                ? "A generated canonical event has no resolvable Logical Track owner."
                : "A generated canonical event has more than one possible Logical Track owner.",
            value.Source));
        return default;
    }

    private static void ValidateBankProgramOrder(
        IReadOnlyList<CanonicalMidiEvent> values,
        List<MidiExportDiagnostic> diagnostics)
    {
        foreach (IGrouping<(long Tick, byte Channel), CanonicalMidiEvent> group in values
            .GroupBy(value => (value.Tick, value.ZeroBasedChannel)))
        {
            int phase = 0;
            foreach (CanonicalMidiEvent value in group)
            {
                MidiMessage message = value.Message;
                if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 0)
                {
                    if (phase != 0) Fail(value);
                }
                else if (message.MessageType == MidiMessageType.ControlChange && message.Byte1 == 32)
                {
                    if (phase > 1) Fail(value);
                    phase = 1;
                }
                else if (message.MessageType == MidiMessageType.ProgramChange)
                {
                    phase = 2;
                }
            }
        }

        void Fail(CanonicalMidiEvent value) => diagnostics.Add(new(
            "MIDORA-MIDI-EXPORT-BANK-ORDER",
            MidiExportDiagnosticCategory.CanonicalConsistency,
            "Bank/Program order must be CC0, then CC32, then Program Change at the same tick.",
            value.Source));
    }

    private static long RelativeTick(CanonicalCompiledResult compiled, long tick)
    {
        if (tick < compiled.StartTick || tick > compiled.EndTick)
        {
            throw new MidoraMidiException(
                $"Conductor event tick {tick} lies outside [{compiled.StartTick}, {compiled.EndTick}].");
        }
        return checked(tick - compiled.StartTick);
    }

    private static MidiExportEncodingResult Failure(List<MidiExportDiagnostic> diagnostics) =>
        new([], diagnostics.ToArray());

    private static TValue GetOrAdd<TKey, TValue>(
        this Dictionary<TKey, TValue> dictionary,
        TKey key)
        where TKey : notnull
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            value = new();
            dictionary.Add(key, value);
        }
        return value;
    }
}
