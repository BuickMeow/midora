using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PagedTimelineCollectionTests
{
    [Fact]
    public void DirectMidiLocalFingerprintRestoresAfterColdRegionEditUndo()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 100_000 };
        DirectMidiNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Key = 60,
            NoteOnVelocity = 100
        };
        segment.Notes.Add(near);
        ulong originalNear = segment.Notes.CreateQuerySnapshot()
            .GetRangeFingerprint(0, 1_000, 0, 127);

        DirectMidiNote far = new(project)
        {
            StartTick = 80_000,
            LengthTicks = 100,
            Key = 64,
            NoteOnVelocity = 100
        };
        segment.Notes.Add(far);
        DirectMidiNoteQuerySnapshot edited = segment.Notes.CreateQuerySnapshot();

        Assert.Equal(
            originalNear,
            edited.GetRangeFingerprint(0, 1_000, 0, 127));
        Assert.True(segment.Notes.Remove(far));
        Assert.Equal(
            originalNear,
            segment.Notes.CreateQuerySnapshot()
                .GetRangeFingerprint(0, 1_000, 0, 127));
    }

    [Fact]
    public void LogicalNotesUseCopyOnWritePageSnapshotsAndRangeQueries()
    {
        MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 20_000,
            ContentOffsetTick = 0
        };
        LogicalNote[] notes = Enumerable.Range(0, 8_500)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();

        segment.Notes.AddRange(notes);
        LogicalNoteQuerySnapshot before = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(3, segment.Notes.PageCount);

        LogicalNote changed = notes[4_200];
        changed.StartTick = 18_000;
        LogicalNoteQuerySnapshot after = segment.Notes.CreateQuerySnapshot();

        LogicalNoteSnapshotValue oldValue = Assert.Single(
            before.QueryValues(8_399, 8_403),
            value => value.Id == changed.Id);
        Assert.Equal(8_400, oldValue.StartTick);
        Assert.DoesNotContain(
            after.QueryValues(8_399, 8_403),
            value => value.Id == changed.Id);
        Assert.Contains(
            after.QueryValues(17_999, 18_003),
            value => value.Id == changed.Id && value.StartTick == 18_000);
    }

    [Fact]
    public void LogicalNoteBatchRemovalIsStableAndAdvancesGenerationOnce()
    {
        MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote[] notes = Enumerable.Range(0, 100_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        long generation = segment.Notes.Generation;
        LogicalNote[] removed = notes.Where((_, index) => (index & 1) == 0).ToArray();

        Assert.Equal(removed.Length, segment.Notes.RemoveRange(removed));

        Assert.Equal(generation + 1, segment.Notes.Generation);
        Assert.Equal(50_000, segment.Notes.Count);
        Assert.Equal(
            Enumerable.Range(0, 50_000).Select(index => index * 2L + 1),
            segment.Notes.Select(static value => value.StartTick));
    }

    [Fact]
    public void SubVoiceEventsUseCopyOnWritePageSnapshotsAndBatchRemoval()
    {
        MidoraProject project = new(192);
        SubVoice voice = new(project);
        TemplateEvent[] events = Enumerable.Range(0, 8_500)
            .Select(index => new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = index * 2L,
                LengthTicks = 2,
                Number = index % 128,
                Value = 100
            })
            .ToArray();
        voice.Events.AddRange(events);
        TemplateEventQuerySnapshot before = voice.Events.CreateQuerySnapshot();
        Assert.Equal(3, voice.Events.PageCount);

        TemplateEvent changed = events[4_200];
        changed.Tick = 18_000;
        TemplateEventQuerySnapshot after = voice.Events.CreateQuerySnapshot();
        Assert.Contains(before.QueryNotes(8_399, 8_403), value => value.Id == changed.Id);
        Assert.DoesNotContain(after.QueryNotes(8_399, 8_403), value => value.Id == changed.Id);
        Assert.Contains(after.QueryNotes(17_999, 18_003), value => value.Id == changed.Id);

        long generation = voice.Events.Generation;
        TemplateEvent[] removed = events.Take(5_000).ToArray();
        Assert.Equal(removed.Length, voice.Events.RemoveRange(removed));
        Assert.Equal(generation + 1, voice.Events.Generation);
        Assert.Equal(3_500, voice.Events.Count);
    }

    [Fact]
    public void ExactCollisionBatchRemovalRestoresPagedOrderAndPageBoundaries()
    {
        MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote[] notes = Enumerable.Range(0, 9_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        LogicalNote[] removed = notes
            .Where((_, index) => index < 4_096 || index >= 4_096 && index % 3 == 0)
            .ToArray();
        int originalPages = segment.Notes.PageCount;

        Action restore = segment.Notes.RemoveRangeForExactCollision(removed);

        Assert.Equal(notes.Length - removed.Length, segment.Notes.Count);
        Assert.DoesNotContain(segment.Notes, removed.Contains);

        restore();

        Assert.Equal(originalPages, segment.Notes.PageCount);
        Assert.Equal(notes, segment.Notes);
        LogicalNoteQuerySnapshot snapshot = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(notes.Length, snapshot.Count);
        Assert.Equal(notes.Select(static value => value.Id), snapshot.EnumerateAll().Select(static value => value.Id));
    }

    [Fact]
    public void LogicalNoteDuplicateAndUndoRemainExactAcrossManyPages()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        LogicalNote[] notes = Enumerable.Range(0, 20_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        track.Segments.Add(segment);
        IPreparedProjectEdit source = ProjectDomainEditCommands.DuplicateLogicalNotes(
                segment.Id,
                notes.Select(static value => value.Id).ToArray(),
                segment.Id,
                newEarliestStartTick: 50_000)
            .Prepare(project);
        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(project, source);

        edit.Apply(project);
        Assert.Equal(40_000, segment.Notes.Count);

        edit.Undo(project);
        Assert.Equal(notes, segment.Notes);
    }

    [Fact]
    public void TemplateNoteDuplicateAndUndoRemainExactAcrossManyPages()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 100_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent[] notes = Enumerable.Range(0, 20_000)
            .Select(index => TemplateEvent.Note(
                project,
                index * 2L,
                lengthTicks: 1,
                note: index % 128,
                velocity: 100))
            .ToArray();
        voice.Events.AddRange(notes);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        IPreparedProjectEdit source = ProjectDomainEditCommands.DuplicateTemplateNotes(
                instrument.Id,
                voice.Id,
                notes.Select(static value => value.Id).ToArray(),
                newEarliestTick: 50_000,
                pitchDelta: 0)
            .Prepare(project);
        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(project, source);

        edit.Apply(project);
        Assert.Equal(40_000, voice.Events.Count);

        edit.Undo(project);
        Assert.Equal(notes, voice.Events);
        Assert.Equal(100_000, instrument.TemplateLengthTicks);
    }
}
