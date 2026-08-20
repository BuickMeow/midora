using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.MidiExport;

namespace Midora.Application.Tests;

public sealed class MidiProjectImportServiceTests
{
    [Fact]
    public void ImportsMultiChannelTrackIntoFixedRootsAndPreservesTrackEnd()
    {
        StandardMidiFileTrack conductor = new(
            480,
            [
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20])
            ]);
        StandardMidiFileTrack source = new(
            960,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Imported"),
                StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [1]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.ControlChange(1, 91, 77)),
                StandardMidiFileEvent.ChannelVoice(240, MidiMessage.NoteOff(0, 60, 45))
            ]);
        byte[] bytes = StandardMidiFile.EncodeType1(192, [conductor, source]);

        MidiProjectImportResult result = MidiProjectImportService.Import(bytes, "Song");

        Assert.DoesNotContain(result.Diagnostics, value => value.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(192, result.Project.TicksPerQuarterNote);
        Assert.Equal("Song", result.Project.Metadata.ProjectName);
        Assert.Equal(2, result.Project.MidiChannelRoots.Count);
        Assert.Equal(2, result.Project.PureMidiTracks.Count);
        Assert.All(result.Project.MidiChannelRoots, root =>
        {
            Assert.Equal(MidiChannelRootRoutingMode.Fixed, root.RoutingMode);
            Assert.Equal((byte)1, root.FixedZeroBasedPort);
        });
        PureMidiTrack noteTrack = result.Project.PureMidiTracks.Single(
            track => track.Segments.SelectMany(segment => segment.Notes).Any());
        MidiSegment noteSegment = Assert.Single(noteTrack.Segments);
        Assert.Equal(960, noteSegment.LengthTicks);
        DirectMidiNote note = Assert.Single(noteSegment.Notes);
        Assert.Equal(240, note.LengthTicks);
        Assert.Equal(45, note.NoteOffVelocity);
        DirectMidiChannelEvent cc = Assert.Single(result.Project.PureMidiTracks
            .SelectMany(track => track.Segments)
            .SelectMany(segment => segment.ChannelEvents));
        Assert.Equal(DirectMidiChannelEventKind.ControlChange, cc.Kind);
        Assert.Equal(91, cc.Data1);
        Assert.Equal(77, cc.Data2);

        CanonicalCompiledResult compiled = new MidoraCompiler().CompileFull(result.Project);
        Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Contains(compiled.Events.ToArray(), value =>
            value.Role == CanonicalEventRole.DirectMidi
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 91);
    }

    [Fact]
    public void UnmatchedNotesArePreservedAsRawEventsWithOneTrackWarning()
    {
        StandardMidiFileTrack source = new(
            100,
            [
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOff(0, 61, 12)),
                StandardMidiFileEvent.ChannelVoice(10, MidiMessage.NoteOn(0, 62, 90))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [source]),
            "Unmatched");

        MidiProjectImportDiagnostic diagnostic = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-UNPAIRED-NOTE");
        Assert.Equal("MIDORA-MIDI-IMPORT-UNPAIRED-NOTE", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(0, diagnostic.SourceTrackIndex);
        Assert.Equal(0, diagnostic.Tick);
        Assert.Equal((byte)0, diagnostic.ZeroBasedPort);
        Assert.Equal((byte)0, diagnostic.ZeroBasedChannel);
        Assert.NotNull(diagnostic.SourceByteOffset);
        MidiSegment segment = Assert.Single(Assert.Single(result.Project.PureMidiTracks).Segments);
        Assert.Empty(segment.Notes);
        Assert.Equal(2, segment.ChannelEvents.Count);
        Assert.All(segment.ChannelEvents, value => Assert.True(
            value.Kind is DirectMidiChannelEventKind.NoteOn
                or DirectMidiChannelEventKind.NoteOff));
    }

    [Fact]
    public void PureConductorTrackDoesNotInventARequiredSourcePort()
    {
        StandardMidiFileTrack conductor = new(
            240,
            [
                StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [127]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20])
            ]);
        StandardMidiFileTrack music = new(
            240,
            [
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [conductor, music]),
            "Conductor Port");

        MidiChannelRoot root = Assert.Single(result.Project.MidiChannelRoots);
        Assert.Equal((byte)0, root.FixedZeroBasedPort);
        Assert.Single(result.Project.PureMidiTracks);
    }

    [Fact]
    public void MidoraPrivateMetadataRestoresAutoRootEmptyTrackAndSourceOrdering()
    {
        MidoraProject source = new(480);
        MidiChannelRoot root = new(source)
        {
            Name = "Restored Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        source.MidiChannelRoots.Add(root);
        source.ArrangementParents.Add(new(ArrangementParentKind.MidiChannelRoot, root.Id));
        PureMidiTrack first = AddTrack(source, root, "First");
        PureMidiTrack empty = AddTrack(source, root, "Empty");
        first.Segments[0].Notes.Add(new(source)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100,
            NoteOffVelocity = 32
        });

        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(
            source,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Round Trip",
            LogicalTracks = []
        });
        Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
        ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(encoded.FileBytes);
        Assert.Equal(3, parsed.Tracks.Count);

        byte[] reordered = StandardMidiFile.EncodeType1(
            parsed.TicksPerQuarterNote,
            [
                ToWritableTrack(parsed.Tracks[0]),
                ToWritableTrack(parsed.Tracks[2]),
                ToWritableTrack(parsed.Tracks[1])
            ]);
        MidiProjectImportResult imported = MidiProjectImportService.Import(
            reordered,
            "Imported");

        MidiChannelRoot importedRoot = Assert.Single(imported.Project.MidiChannelRoots);
        Assert.Equal("Restored Root", importedRoot.Name);
        Assert.Equal(MidiChannelRootRoutingMode.Auto, importedRoot.RoutingMode);
        Assert.Equal(MidiChannelMode.Melodic, importedRoot.ChannelMode);
        Assert.Equal(
            ["First", "Empty"],
            importedRoot.MidiTrackIds
                .Select(id => imported.Project.PureMidiTracks.Single(track => track.Id == id).Name)
                .ToArray());
        PureMidiTrack importedEmpty = imported.Project.PureMidiTracks.Single(
            track => track.Name == "Empty");
        Assert.Empty(Assert.Single(importedEmpty.Segments).Notes);
    }

    [Fact]
    public void MidoraChannel10InitializationDoesNotBecomeDuplicatedOpaqueSourceData()
    {
        MidoraProject source = new(480);
        MidiChannelRoot root = new(source)
        {
            Name = "Melodic Channel 10",
            RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0,
            FixedZeroBasedChannel = 9,
            ChannelMode = MidiChannelMode.Melodic
        };
        source.MidiChannelRoots.Add(root);
        source.ArrangementParents.Add(new(ArrangementParentKind.MidiChannelRoot, root.Id));
        PureMidiTrack track = AddTrack(source, root, "Channel 10 Track");
        track.Segments[0].Notes.Add(new(source)
        {
            StartTick = 0,
            LengthTicks = 120,
            Key = 60,
            NoteOnVelocity = 100
        });

        byte[] firstExport = Export(source);
        Assert.Equal(2, StandardMidiFile.ParseType0Or1(firstExport).Tracks[1].Events.Count(
            value => value.Kind == StandardMidiFileEventKind.SystemExclusive));

        MidiProjectImportResult imported = MidiProjectImportService.Import(firstExport, "Imported");
        MidiSegment importedSegment = Assert.Single(Assert.Single(imported.Project.PureMidiTracks).Segments);
        Assert.Empty(importedSegment.OpaqueEvents);

        byte[] secondExport = Export(imported.Project);
        Assert.Equal(2, StandardMidiFile.ParseType0Or1(secondExport).Tracks[1].Events.Count(
            value => value.Kind == StandardMidiFileEventKind.SystemExclusive));
    }

    [Fact]
    public void ImportsFormatZeroRunningStatusAndSplitsItsChannels()
    {
        byte[] source = Convert.FromHexString(
            "4D54686400000006000000010060"
            + "4D54726B00000012"
            + "00903C64103C00"
            + "00914064104000"
            + "00FF2F00");

        MidiProjectImportResult result = MidiProjectImportService.Import(source, "Format Zero");

        Assert.DoesNotContain(result.Diagnostics, value => value.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(2, result.Project.MidiChannelRoots.Count);
        Assert.Equal(2, result.Project.PureMidiTracks.Count);
        Assert.Equal(
            new byte[] { 0, 1 },
            result.Project.MidiChannelRoots
                .Select(root => root.FixedZeroBasedChannel)
                .Order()
                .ToArray());
        Assert.All(result.Project.PureMidiTracks, track =>
        {
            MidiSegment segment = Assert.Single(track.Segments);
            Assert.Equal(32, segment.LengthTicks);
            Assert.Single(segment.Notes);
        });
    }

    [Fact]
    public void PortChangesSplitOneSourceTrackAndOpaqueEventsHaveOneOwner()
    {
        StandardMidiFileTrack source = new(
            120,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Ports"),
                StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [2]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.ControlChange(0, 7, 90)),
                StandardMidiFileEvent.Meta(40, StandardMidiFile.MidiPortMetaType, [4]),
                StandardMidiFileEvent.ChannelVoice(40, MidiMessage.ProgramChange(0, 12)),
                StandardMidiFileEvent.SystemExclusive(60, [0x7d, 0x01, 0xf7])
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [source]),
            "Port Changes");

        Assert.DoesNotContain(result.Diagnostics, value => value.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(
            new byte[] { 2, 4 },
            result.Project.MidiChannelRoots
                .Select(root => root.FixedZeroBasedPort)
                .Order()
                .ToArray());
        Assert.Equal(2, result.Project.PureMidiTracks.Count);
        Assert.All(result.Project.PureMidiTracks, track =>
            Assert.Equal(120, Assert.Single(track.Segments).LengthTicks));
        Assert.Equal(1, result.Project.PureMidiTracks
            .SelectMany(track => track.Segments)
            .Sum(segment => segment.OpaqueEvents.Count));
        PureMidiTrack opaqueOwner = Assert.Single(result.Project.PureMidiTracks, track =>
            track.Segments.SelectMany(segment => segment.OpaqueEvents).Any());
        MidiChannelRoot ownerRoot = result.Project.MidiChannelRoots.Single(
            root => root.Id == opaqueOwner.MidiChannelRootId);
        Assert.Equal((byte)4, ownerRoot.FixedZeroBasedPort);
    }

    [Fact]
    public void OutOfRangeSourcePortRequiresExplicitOneToOneMapping()
    {
        StandardMidiFileTrack source = new(
            120,
            [
                StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [127]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);
        byte[] bytes = StandardMidiFile.EncodeType1(480, [source]);

        MidiImportPortMappingRequiredException required = Assert.Throws<
            MidiImportPortMappingRequiredException>(() =>
                MidiProjectImportService.Import(bytes, "Port Mapping"));
        Assert.Equal(new byte[] { 127 }, required.SourcePorts);

        MidiProjectImportResult mapped = MidiProjectImportService.Import(
            bytes,
            "Port Mapping",
            new Dictionary<byte, byte> { [127] = 15 });
        Assert.Equal((byte)15, Assert.Single(mapped.Project.MidiChannelRoots).FixedZeroBasedPort);
    }

    [Fact]
    public void MissingConductorInitialStatesAreMadeExplicitAndReported()
    {
        StandardMidiFileTrack source = new(
            120,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Music"),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [source]),
            "Defaults");

        TempoChange tempo = Assert.Single(result.Project.Conductor.Tempos);
        Assert.Equal(0, tempo.Tick);
        Assert.Equal(120m, tempo.BeatsPerMinute);
        TimeSignatureChange timeSignature = Assert.Single(result.Project.Conductor.TimeSignatures);
        Assert.Equal(0, timeSignature.Tick);
        Assert.Equal(4, timeSignature.Numerator);
        Assert.Equal(4, timeSignature.Denominator);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-DEFAULT-TEMPO"
            && value.Severity == DiagnosticSeverity.Info);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-DEFAULT-TIME-SIGNATURE"
            && value.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public void DuplicateTempoUsesLastSourceOrderAndReportsRedundantAndConflictingValues()
    {
        StandardMidiFileTrack conductor = new(
            120,
            [
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.TimeSignatureMetaType,
                    [4, 2, 24, 8])
            ]);
        StandardMidiFileTrack source = new(
            120,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Music"),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x0f, 0x42, 0x40]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [conductor, source]),
            "Tempo Compatibility");

        TempoChange tempo = Assert.Single(result.Project.Conductor.Tempos);
        Assert.Equal(0, tempo.Tick);
        Assert.Equal(60m, tempo.BeatsPerMinute);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-REDUNDANT-TEMPO"
            && value.Severity == DiagnosticSeverity.Info);
        MidiProjectImportDiagnostic conflict = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-CONFLICTING-TEMPO");
        Assert.Equal(DiagnosticSeverity.Warning, conflict.Severity);
        Assert.Equal(1, conflict.SourceTrackIndex);
        Assert.Equal(0, conflict.Tick);
        Assert.Contains("120 BPM", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("60 BPM", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateTimeAndKeySignaturesUseLastSourceOrderAndRemainValid()
    {
        StandardMidiFileTrack conductor = new(
            120,
            [
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.TimeSignatureMetaType,
                    [4, 2, 24, 8]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.KeySignatureMetaType,
                    [0, 0])
            ]);
        StandardMidiFileTrack source = new(
            120,
            [
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.TimeSignatureMetaType,
                    [4, 2, 24, 8]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.TimeSignatureMetaType,
                    [3, 2, 24, 8]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.KeySignatureMetaType,
                    [0, 0]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.KeySignatureMetaType,
                    [1, 1]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [conductor, source]),
            "Conductor Compatibility");

        TimeSignatureChange timeSignature = Assert.Single(result.Project.Conductor.TimeSignatures);
        Assert.Equal((3, 4), (timeSignature.Numerator, timeSignature.Denominator));
        KeySignatureChange keySignature = Assert.Single(result.Project.Conductor.KeySignatures);
        Assert.Equal((1, true), (keySignature.SharpsFlats, keySignature.IsMinor));
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-TIME-SIGNATURE"
            && value.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-KEY-SIGNATURE"
            && value.Severity == DiagnosticSeverity.Warning);
        using MidoraCompiler compiler = new();
        Assert.True(compiler.CompileFull(result.Project).IsConsumable);
    }

    [Fact]
    public void InvalidAndBlankTrackNamesAreDiscardedAndReceiveDeterministicFallbacks()
    {
        StandardMidiFileTrack conductor = new(
            120,
            [
                StandardMidiFileEvent.Meta(0, StandardMidiFile.TrackNameMetaType, [0xff]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.SetTempoMetaType,
                    [0x07, 0xa1, 0x20]),
                StandardMidiFileEvent.Meta(
                    0,
                    StandardMidiFile.TimeSignatureMetaType,
                    [4, 2, 24, 8])
            ]);
        StandardMidiFileTrack invalidName = new(
            120,
            [
                StandardMidiFileEvent.Meta(0, StandardMidiFile.TrackNameMetaType, [0xc3, 0x28]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
            ]);
        StandardMidiFileTrack blankName = new(
            120,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "   "),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(1, 64, 100)),
                StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(1, 64, 0))
            ]);

        MidiProjectImportResult result = MidiProjectImportService.Import(
            StandardMidiFile.EncodeType1(480, [conductor, invalidName, blankName]),
            "Track Name Compatibility");

        Assert.Equal(
            ["MIDI Track 2", "MIDI Track 3"],
            result.Project.PureMidiTracks.Select(value => value.Name).ToArray());
        MidiProjectImportDiagnostic invalid = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-INVALID-TRACK-NAME");
        Assert.Equal(DiagnosticSeverity.Info, invalid.Severity);
        Assert.Equal(0, invalid.SourceTrackIndex);
        Assert.NotNull(invalid.SourceByteOffset);
        MidiProjectImportDiagnostic fallback = Assert.Single(result.Diagnostics, value =>
            value.Code == "MIDORA-MIDI-IMPORT-FALLBACK-TRACK-NAME");
        Assert.Equal(DiagnosticSeverity.Info, fallback.Severity);
        Assert.Equal(2, result.Project.PureMidiTracks.Count);
    }

    [Fact]
    public void InvalidUtf8InOtherModeledTextMetaStillFailsImport()
    {
        StandardMidiFileTrack source = new(
            0,
            [StandardMidiFileEvent.Meta(0, StandardMidiFile.MarkerMetaType, [0xff])]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            MidiProjectImportService.Import(
                StandardMidiFile.EncodeType1(480, [source]),
                "Invalid Marker"));

        Assert.Contains("Marker is not strict UTF-8", exception.Message, StringComparison.Ordinal);
    }

    private static PureMidiTrack AddTrack(
        MidoraProject project,
        MidiChannelRoot root,
        string name)
    {
        PureMidiTrack track = new(project)
        {
            Name = name,
            MidiChannelRootId = root.Id
        };
        track.Segments.Add(new(project) { LengthTicks = 960 });
        project.PureMidiTracks.Add(track);
        root.MidiTrackIds.Add(track.Id);
        return track;
    }

    private static StandardMidiFileTrack ToWritableTrack(
        ParsedStandardMidiFileTrack source) => new(
            source.EndTick,
            source.Events.Select(value => value.Kind switch
            {
                StandardMidiFileEventKind.ChannelVoice =>
                    StandardMidiFileEvent.ChannelVoice(value.Tick, value.Message),
                StandardMidiFileEventKind.Meta =>
                    StandardMidiFileEvent.Meta(value.Tick, value.Type, value.Data.Span),
                StandardMidiFileEventKind.SystemExclusive =>
                    StandardMidiFileEvent.SystemExclusive(
                        value.Tick,
                        value.Data.Span,
                        continuation: value.Type == 0xf7),
                _ => throw new InvalidOperationException(
                    $"Unknown SMF event kind {value.Kind}.")
            }));

    private static byte[] Export(MidoraProject project)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(
            project,
            new CompilationRequest { Purpose = CompilationPurpose.MidiExport });
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(new()
        {
            CompiledResult = compiled,
            ConductorTrackName = "Round Trip",
            LogicalTracks = []
        });
        Assert.True(encoded.Succeeded, string.Join(Environment.NewLine, encoded.Diagnostics));
        return encoded.FileBytes;
    }
}
