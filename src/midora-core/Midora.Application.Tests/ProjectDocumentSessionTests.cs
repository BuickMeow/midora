using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectDocumentSessionTests
{
    [Fact]
    public void FreshUnsavedDocumentNeedsSaveWithoutBeingModified()
    {
        using ProjectCompilationSession compilation = new(CreateProject());
        ProjectDocumentSession document = new(compilation);

        Assert.False(document.HasPersistentOrigin);
        Assert.False(document.IsModified);
        Assert.True(document.NeedsSaveBeforeClose);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);

        document.MarkSaveSucceeded();

        Assert.True(document.HasPersistentOrigin);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
    }

    [Fact]
    public void PropertyEditUndoAndRedoKeepCompilationAndHistoryInSync()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult original = compilation.LastAttempt;
        int compilationEvents = 0;
        int historyEvents = 0;
        List<ProjectContentChangedEventArgs> contentEvents = [];
        compilation.CompilationChanged += (_, _) => compilationEvents++;
        document.HistoryChanged += (_, _) => historyEvents++;
        document.ContentChanged += (_, args) => contentEvents.Add(args);

        ProjectEditExecution edit = document.Execute(SetPitch(track.Id, note.Id, 72));

        Assert.True(edit.Changed);
        Assert.True(document.IsModified);
        Assert.True(document.NeedsSaveBeforeClose);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Equal("Change note pitch", document.UndoName);
        Assert.Equal(72, note.Note);
        Assert.NotEqual(original.Fingerprint, edit.CompilationResult.Fingerprint);

        CanonicalCompiledResult undone = document.Undo();

        Assert.Equal(60, note.Note);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
        Assert.Equal("Change note pitch", document.RedoName);
        AssertFormallyEqual(original, undone);

        CanonicalCompiledResult redone = document.Redo();

        Assert.Equal(72, note.Note);
        Assert.True(document.IsModified);
        Assert.Equal(edit.CompilationResult.Fingerprint, redone.Fingerprint);
        Assert.Equal(3, compilationEvents);
        Assert.Equal(3, historyEvents);
        Assert.Equal(3, contentEvents.Count);
        Assert.All(contentEvents, change => Assert.Contains(track.Id, change.TrackIds));
    }

    [Fact]
    public void NoOpDoesNotCompileCreateHistoryOrMarkModified()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;
        int events = 0;
        document.HistoryChanged += (_, _) => events++;

        ProjectEditExecution result = document.Execute(SetVelocity(track.Id, note.Id, note.Velocity));

        Assert.False(result.Changed);
        Assert.Same(before, result.CompilationResult);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SavePointTracksUndoBackToSavedStateAndSaveCopyNeedsNoMutation()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));

        document.MarkSaveSucceeded();
        Assert.False(document.IsModified);
        document.Execute(SetVelocity(track.Id, note.Id, 90));
        Assert.True(document.IsModified);

        document.Undo();

        Assert.Equal(80, note.Velocity);
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.True(document.CanRedo);
    }

    [Fact]
    public void EditingAfterUndoDiscardsRedoBranchAndCannotReachDiscardedSavePoint()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        document.MarkSaveSucceeded();
        document.Undo();
        Assert.True(document.IsModified);

        document.Execute(SetVelocity(track.Id, note.Id, 70));

        Assert.False(document.CanRedo);
        Assert.True(document.IsModified);
        Assert.Single(document.History);
        Assert.Equal(70, note.Velocity);
    }

    [Fact]
    public void ExternalDirtyReasonSurvivesUndoAndClearsOnlyAfterSuccessfulSave()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.MarkExternallyModified("RecoveredConductorDefaults");
        document.MarkExternallyModified("RecoveredConductorDefaults");
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        document.Undo();

        Assert.True(document.IsModified);
        Assert.Equal(["RecoveredConductorDefaults"], document.ExternalDirtyReasons);

        document.MarkSaveSucceeded();

        Assert.False(document.IsModified);
        Assert.Empty(document.ExternalDirtyReasons);
    }

    [Fact]
    public void ProjectEditLockRejectsExecuteUndoAndRedoWithoutMutatingHistory()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(SetVelocity(track.Id, note.Id, 80));
        using IDisposable editLock = compilation.AcquireProjectEditLock();

        Assert.Throws<InvalidOperationException>(() => document.Undo());
        Assert.Throws<InvalidOperationException>(() =>
            document.Execute(SetVelocity(track.Id, note.Id, 70)));

        Assert.Equal(80, note.Velocity);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Single(document.History);
    }

    [Fact]
    public void FailedApplyRollsBackProjectAndCompilerWithoutCreatingHistory()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            document.Execute(new FailingVelocityCommand(track.Id, note.Id)));

        Assert.Equal("Injected edit failure.", error.Message);
        Assert.Equal(100, note.Velocity);
        AssertFormallyEqual(before, compilation.LastAttempt);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void InvalidCommandNameAndUnavailableUndoRedoFailBeforeMutation()
    {
        using ProjectCompilationSession compilation = new(CreateProject());
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        Assert.Throws<InvalidOperationException>(document.Undo);
        Assert.Throws<InvalidOperationException>(document.Redo);
        Assert.Throws<ArgumentException>(() => document.Execute(new EmptyNameCommand()));
        Assert.Empty(document.History);
    }

    [Fact]
    public void ChangeNotificationsCannotReenterProjectHistoryMutation()
    {
        MidoraProject project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        LogicalNote note = track.Segments[0].Notes[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        bool rejected = false;
        document.HistoryChanged += (_, _) =>
        {
            _ = Assert.Throws<InvalidOperationException>(() =>
                document.Execute(SetVelocity(track.Id, note.Id, 90)));
            rejected = true;
        };

        document.Execute(SetVelocity(track.Id, note.Id, 80));

        Assert.True(rejected);
        Assert.Equal(80, note.Velocity);
        Assert.Single(document.History);
    }

    private static ProjectPropertyEditCommand<int> SetVelocity(
        MidoraId trackId,
        MidoraId noteId,
        int velocity)
    {
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(trackId);
        return new(
            "Change note velocity",
            project => FindNote(project, trackId, noteId).Velocity,
            (project, value) => FindNote(project, trackId, noteId).Velocity = value,
            velocity,
            changes);
    }

    private static ProjectPropertyEditCommand<int> SetPitch(
        MidoraId trackId,
        MidoraId noteId,
        int note)
    {
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(trackId);
        return new(
            "Change note pitch",
            project => FindNote(project, trackId, noteId).Note,
            (project, value) => FindNote(project, trackId, noteId).Note = value,
            note,
            changes);
    }

    private static LogicalNote FindNote(MidoraProject project, MidoraId trackId, MidoraId noteId) =>
        project.Tracks.Single(track => track.Id == trackId)
            .Segments.SelectMany(segment => segment.Notes)
            .Single(note => note.Id == noteId);

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Piano",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 480, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track", EventInstrumentId = instrument.Id };
        Segment segment = new(project) { LengthTicks = 960 };
        segment.Notes.Add(new LogicalNote(project)
        {
            LengthTicks = 480,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return project;
    }

    private static void AssertFormallyEqual(
        CanonicalCompiledResult expected,
        CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Conductor.Tempos.ToArray(), actual.Conductor.Tempos.ToArray());
        Assert.Equal(
            expected.Conductor.TimeSignatures.ToArray(),
            actual.Conductor.TimeSignatures.ToArray());
        Assert.Equal(
            expected.Conductor.KeySignatures.ToArray(),
            actual.Conductor.KeySignatures.ToArray());
        Assert.Equal(expected.Conductor.Markers.ToArray(), actual.Conductor.Markers.ToArray());
        Assert.Equal(expected.Conductor.EndMarker, actual.Conductor.EndMarker);
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private sealed class FailingVelocityCommand(MidoraId trackId, MidoraId noteId)
        : IProjectEditCommand
    {
        public string Name => "Failing velocity change";

        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            new FailingPrepared(trackId, noteId, FindNote(project, trackId, noteId).Velocity);

        private sealed class FailingPrepared(
            MidoraId trackId,
            MidoraId noteId,
            int oldVelocity) : IPreparedProjectEdit
        {
            public bool HasChanges => true;
            public ProjectChangeSet Changes { get; } = TrackChange(trackId);

            public void Apply(MidoraProject project)
            {
                FindNote(project, trackId, noteId).Velocity = 1;
                throw new InvalidOperationException("Injected edit failure.");
            }

            public void Undo(MidoraProject project) =>
                FindNote(project, trackId, noteId).Velocity = oldVelocity;

            private static ProjectChangeSet TrackChange(MidoraId id)
            {
                ProjectChangeSet result = new();
                result.TrackIds.Add(id);
                return result;
            }
        }
    }

    private sealed class EmptyNameCommand : IProjectEditCommand
    {
        public string Name => " ";
        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            throw new InvalidOperationException("Prepare must not run for an invalid name.");
    }
}
