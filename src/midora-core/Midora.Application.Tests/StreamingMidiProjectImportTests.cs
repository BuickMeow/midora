using Midora.Domain;
using Midora.Midi;

namespace Midora.Application.Tests;

public sealed class StreamingMidiProjectImportTests
{
    [Fact]
    public void ImportFileUsesPagedContentAndPreservesChannelSemantics()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.mid");
        MidiProjectImportResult? result = null;
        try
        {
            byte[] bytes = StandardMidiFile.EncodeType1(
                192,
                [
                    new StandardMidiFileTrack(
                        384,
                        [StandardMidiFileEvent.Meta(0, StandardMidiFile.SetTempoMetaType, [0x07, 0xa1, 0x20])]),
                    new StandardMidiFileTrack(
                        384,
                        [
                            StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Piano"),
                            StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [0]),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.ProgramChange(0, 4)),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                            StandardMidiFileEvent.ChannelVoice(96, MidiMessage.NoteOff(0, 60, 23)),
                            StandardMidiFileEvent.Meta(100, 0x01, [1, 2, 3])
                        ])
                ]);
            File.WriteAllBytes(path, bytes);

            RecordingProgress progress = new();
            result = MidiProjectImportService.ImportFile(
                path,
                "Song",
                progress: progress);

            PureMidiTrack track = Assert.Single(result.Project.PureMidiTracks);
            MidiSegment segment = Assert.Single(track.Segments);
            Assert.True(segment.UsesPagedContent);
            DirectMidiNote note = Assert.Single(segment.Notes);
            Assert.Equal(60, note.Key);
            Assert.Equal(23, note.NoteOffVelocity);
            Assert.Equal(4, Assert.Single(segment.ChannelEvents).Data1);
            Assert.Equal([1, 2, 3], Assert.Single(segment.OpaqueEvents).Payload);
            Assert.NotNull(result.Metrics);
            Assert.Equal(1, result.Metrics.ImportedNoteCount);
            Assert.True(result.Metrics.ContentPageCount >= 3);
            Assert.True(result.Metrics.ContentPackBytes > 0);
            Assert.Equal(MidiProjectImportPhase.ScanningSource, progress.Values[0].Phase);
            Assert.Equal(0, progress.Values[0].Fraction);
            Assert.Contains(progress.Values, value =>
                value.Phase == MidiProjectImportPhase.ImportingEvents
                && value.ProcessedEventCount == value.TotalEventCount
                && value.TotalEventCount == result.Metrics.ScannedEventCount);
            Assert.Equal(
                MidiProjectImportPhase.FinalizingProject,
                progress.Values[^1].Phase);
            Assert.Equal(0.98, progress.Values[^1].Fraction);
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportFileNormalizesDuplicateConductorStatesBeforeValidation()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "duplicates.mid");
        MidiProjectImportResult? result = null;
        try
        {
            File.WriteAllBytes(path, StandardMidiFile.EncodeType1(
                480,
                [
                    new StandardMidiFileTrack(
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
                        ]),
                    new StandardMidiFileTrack(
                        120,
                        [
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.TimeSignatureMetaType,
                                [3, 2, 24, 8]),
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.KeySignatureMetaType,
                                [1, 1]),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                            StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
                        ])
                ]));

            result = MidiProjectImportService.ImportFile(path, "Duplicates");

            TimeSignatureChange timeSignature = Assert.Single(
                result.Project.Conductor.TimeSignatures);
            Assert.Equal((3, 4), (timeSignature.Numerator, timeSignature.Denominator));
            KeySignatureChange keySignature = Assert.Single(
                result.Project.Conductor.KeySignatures);
            Assert.Equal((1, true), (keySignature.SharpsFlats, keySignature.IsMinor));
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-TIME-SIGNATURE");
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-KEY-SIGNATURE");
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingProgress : IProgress<MidiProjectImportProgress>
    {
        public List<MidiProjectImportProgress> Values { get; } = [];

        public void Report(MidiProjectImportProgress value) => Values.Add(value);
    }
}
