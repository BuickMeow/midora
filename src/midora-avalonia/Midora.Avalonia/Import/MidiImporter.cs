using System.Text;
using Midora.Midi;

namespace Midora.Avalonia.Import;

public static class MidiImporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding? CodePage932;

    static MidiImporter()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            CodePage932 = Encoding.GetEncoding(
                932,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (Exception)
        {
            CodePage932 = null;
        }
    }

    public static ImportedMidiProject Import(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string sourceFileName = Path.GetFileName(path);
        byte[] bytes;
        try
        {
            FileInfo info = new(path);
            if (info.Length > StandardMidiFile.MaximumImportFileByteCount)
            {
                throw CreateTooLargeException(sourceFileName);
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException exception)
        {
            throw new MidiImportException(
                $"Could not read MIDI file '{sourceFileName}': {exception.Message}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MidiImportException(
                $"Could not read MIDI file '{sourceFileName}': {exception.Message}",
                exception);
        }
        if (bytes.Length > StandardMidiFile.MaximumImportFileByteCount)
        {
            throw CreateTooLargeException(sourceFileName);
        }
        return Import(bytes, sourceFileName);
    }

    public static ImportedMidiProject Import(ReadOnlySpan<byte> bytes, string sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(sourceFileName);
        if (bytes.Length > StandardMidiFile.MaximumImportFileByteCount)
        {
            throw CreateTooLargeException(sourceFileName);
        }

        ParsedStandardMidiFile parsed;
        try
        {
            parsed = StandardMidiFile.ParseType0Or1(bytes);
        }
        catch (MidoraMidiException exception)
        {
            throw new MidiImportException(
                $"'{sourceFileName}' is not a supported Standard MIDI File: {exception.Message}",
                exception);
        }

        List<ImportedMidiConductorEvent> conductor = [];
        ImportedMidiTrack[] tracks = new ImportedMidiTrack[parsed.Tracks.Count];
        long maximumEndTick = 0;
        for (int index = 0; index < parsed.Tracks.Count; index++)
        {
            ParsedStandardMidiFileTrack source = parsed.Tracks[index];
            tracks[index] = BuildTrack(source, conductor);
            if (source.EndTick > maximumEndTick)
            {
                maximumEndTick = source.EndTick;
            }
        }

        ImportedMidiConductorEvent[] conductorEvents = conductor
            .OrderBy(item => item.Tick)
            .ToArray();
        return new ImportedMidiProject(
            parsed.Format,
            parsed.TicksPerQuarterNote,
            maximumEndTick,
            tracks,
            conductorEvents)
        {
            SourceFileName = sourceFileName,
        };
    }

    private static ImportedMidiTrack BuildTrack(
        ParsedStandardMidiFileTrack source,
        List<ImportedMidiConductorEvent> conductor)
    {
        List<ImportedMidiNote> notes = [];
        List<ImportedMidiEvent> events = [];
        Dictionary<int, Queue<(long StartTick, int Velocity)>> openNotes = [];
        int channelMask = 0;
        string? trackName = null;

        foreach (ParsedStandardMidiFileEvent item in source.Events)
        {
            switch (item.Kind)
            {
                case StandardMidiFileEventKind.ChannelVoice:
                    channelMask |= 1 << item.Message.ChannelNumber;
                    HandleChannelVoice(item, notes, events, openNotes);
                    break;
                case StandardMidiFileEventKind.Meta:
                    HandleMeta(item, conductor, ref trackName);
                    break;
            }
        }

        foreach (int pair in openNotes.Keys.Order())
        {
            Queue<(long StartTick, int Velocity)> queue = openNotes[pair];
            while (queue.Count > 0)
            {
                (long startTick, int velocity) = queue.Dequeue();
                if (source.EndTick > startTick)
                {
                    notes.Add(new ImportedMidiNote(
                        startTick,
                        source.EndTick,
                        pair & 0xff,
                        velocity,
                        0,
                        pair >> 8));
                }
            }
        }

        ImportedMidiNote[] sortedNotes = notes
            .OrderBy(note => note.StartTick)
            .ThenBy(note => note.Key)
            .ToArray();
        ImportedMidiEvent[] sortedEvents = events
            .OrderBy(value => value.Tick)
            .ToArray();
        return new ImportedMidiTrack(
            source.SourceTrackIndex,
            trackName ?? $"Track {source.SourceTrackIndex + 1}",
            channelMask,
            source.EndTick,
            sortedNotes,
            sortedEvents);
    }

    private static void HandleChannelVoice(
        ParsedStandardMidiFileEvent item,
        List<ImportedMidiNote> notes,
        List<ImportedMidiEvent> events,
        Dictionary<int, Queue<(long StartTick, int Velocity)>> openNotes)
    {
        MidiMessage message = item.Message;
        int channel = message.ChannelNumber;
        int pair = (channel << 8) | message.Byte1;
        switch (message.MessageType)
        {
            case MidiMessageType.NoteOn when message.Byte2 > 0:
                if (!openNotes.TryGetValue(
                    pair,
                    out Queue<(long StartTick, int Velocity)>? queue))
                {
                    queue = new Queue<(long StartTick, int Velocity)>();
                    openNotes.Add(pair, queue);
                }
                queue.Enqueue((item.Tick, message.Byte2));
                break;
            case MidiMessageType.NoteOff:
                CloseNote(notes, openNotes, pair, item.Tick, message.Byte2);
                break;
            case MidiMessageType.NoteOn:
                CloseNote(notes, openNotes, pair, item.Tick, 0);
                break;
            case MidiMessageType.PolyphonicKeyPressure:
                events.Add(CreateEvent(
                    item.Tick,
                    channel,
                    ImportedMidiEventKind.PolyAfterTouch,
                    message.Byte1,
                    message.Byte2,
                    message.Byte0));
                break;
            case MidiMessageType.ControlChange:
                events.Add(CreateEvent(
                    item.Tick,
                    channel,
                    message.Byte1 >= 120
                        ? ImportedMidiEventKind.ChannelMode
                        : ImportedMidiEventKind.ControlChange,
                    message.Byte1,
                    message.Byte2,
                    message.Byte0));
                break;
            case MidiMessageType.ProgramChange:
                events.Add(CreateEvent(
                    item.Tick,
                    channel,
                    ImportedMidiEventKind.ProgramChange,
                    message.Byte1,
                    0,
                    message.Byte0));
                break;
            case MidiMessageType.ChannelPressure:
                events.Add(CreateEvent(
                    item.Tick,
                    channel,
                    ImportedMidiEventKind.AfterTouch,
                    message.Byte1,
                    0,
                    message.Byte0));
                break;
            case MidiMessageType.PitchWheelChange:
                events.Add(CreateEvent(
                    item.Tick,
                    channel,
                    ImportedMidiEventKind.PitchBend,
                    message.Byte1,
                    message.Byte2,
                    message.Byte0));
                break;
        }
    }

    private static void CloseNote(
        List<ImportedMidiNote> notes,
        Dictionary<int, Queue<(long StartTick, int Velocity)>> openNotes,
        int pair,
        long tick,
        int noteOffVelocity)
    {
        if (!openNotes.TryGetValue(
            pair,
            out Queue<(long StartTick, int Velocity)>? queue)
            || queue.Count == 0)
        {
            return;
        }

        (long startTick, int velocity) = queue.Dequeue();
        if (tick > startTick)
        {
            notes.Add(new ImportedMidiNote(
                startTick,
                tick,
                pair & 0xff,
                velocity,
                noteOffVelocity,
                pair >> 8));
        }
    }

    private static void HandleMeta(
        ParsedStandardMidiFileEvent item,
        List<ImportedMidiConductorEvent> conductor,
        ref string? trackName)
    {
        ReadOnlySpan<byte> data = item.Data.Span;
        switch (item.Type)
        {
            case StandardMidiFile.TrackNameMetaType:
                if (trackName is null && TryDecodeText(data) is { Length: > 0 } name)
                {
                    trackName = name;
                }
                break;
            case StandardMidiFile.SetTempoMetaType:
                if (data.Length == 3)
                {
                    int microsecondsPerQuarter
                        = (data[0] << 16) | (data[1] << 8) | data[2];
                    if (microsecondsPerQuarter > 0)
                    {
                        conductor.Add(new ImportedMidiConductorEvent(
                            item.Tick,
                            ImportedConductorKind.Tempo,
                            60_000_000d / microsecondsPerQuarter,
                            0,
                            0,
                            string.Empty));
                    }
                }
                break;
            case StandardMidiFile.TimeSignatureMetaType:
                if (data.Length >= 2)
                {
                    conductor.Add(new ImportedMidiConductorEvent(
                        item.Tick,
                        ImportedConductorKind.TimeSignature,
                        0d,
                        data[0],
                        data[1] < 31 ? 1 << data[1] : 0,
                        string.Empty));
                }
                break;
            case StandardMidiFile.MarkerMetaType:
                conductor.Add(new ImportedMidiConductorEvent(
                    item.Tick,
                    ImportedConductorKind.Marker,
                    0d,
                    0,
                    0,
                    TryDecodeText(data) ?? string.Empty));
                break;
            case StandardMidiFile.KeySignatureMetaType:
                if (data.Length >= 2)
                {
                    int accidentals = (sbyte)data[0];
                    conductor.Add(new ImportedMidiConductorEvent(
                        item.Tick,
                        ImportedConductorKind.KeySignature,
                        0d,
                        accidentals,
                        data[1],
                        string.Empty));
                }
                break;
        }
    }

    private static ImportedMidiEvent CreateEvent(
        long tick,
        int channel,
        ImportedMidiEventKind kind,
        int data1,
        int data2,
        int rawStatus)
    {
        return new ImportedMidiEvent(tick, channel, kind, data1, data2)
        {
            RawStatus = rawStatus,
        };
    }

    private static string? TryDecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
        }

        Encoding? codePage932 = CodePage932;
        if (codePage932 is not null)
        {
            try
            {
                return codePage932.GetString(data);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return null;
    }

    private static MidiImportException CreateTooLargeException(string sourceFileName)
    {
        return new MidiImportException(
            $"'{sourceFileName}' is larger than the supported "
            + $"{StandardMidiFile.MaximumImportFileByteCount}-byte MIDI import limit.");
    }
}
