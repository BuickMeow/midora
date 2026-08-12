using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class ProjectCompilationSessionBackgroundTests
{
    [Fact]
    public async Task BackgroundEditPublishesOnlyTheCurrentSourceRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(10));
        CanonicalCompiledResult previous = session.LastAttempt;

        CanonicalCompiledResult editReturn = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 240, "A")),
            new ProjectChangeSet { AffectsConductor = true });

        Assert.Same(previous, editReturn);
        Assert.Equal(1, session.SourceRevision);
        Assert.False(session.IsCompilationCurrent);
        Assert.Contains(
            session.CompilationState,
            new[] { ProjectCompilationState.Outdated, ProjectCompilationState.Compiling });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Conductor.Markers.ToArray(), current.Conductor.Markers.ToArray());
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task ConsecutiveEditsConvergeToTheLatestRevision()
    {
        MidoraProject project = new(480);
        project.Conductor.EndMarker = new ProjectEndMarker(project, 960);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(25));

        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "First")),
            new ProjectChangeSet { AffectsConductor = true });
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 360, "Second")),
            new ProjectChangeSet { AffectsConductor = true });

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(2, session.CompiledRevision);
        Assert.True(session.IsCompilationCurrent);
        CanonicalMarker[] markers = current.Conductor.Markers.ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Contains(markers, value => value.Name == "First");
        Assert.Contains(markers, value => value.Name == "Second");
    }

    [Fact]
    public async Task WaitingConsumerCanCancelWithoutCancelingSharedCompilation()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromMilliseconds(30));
        _ = session.ApplyEdit(
            value => value.Conductor.Markers.Add(new ProjectMarker(value, 120, "Marker")),
            new ProjectChangeSet { AffectsConductor = true });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.EnsureCurrentCompilationAsync(cancellation.Token));

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        Assert.True(current.IsConsumable);
        Assert.True(session.IsCompilationCurrent);
    }

    [Fact]
    public async Task EditArrivingAfterWorkerStartsSupersedesTheOlderRevision()
    {
        (MidoraProject project, LogicalTrack track, LogicalNote first, LogicalNote second) =
            CreateNoteProject(2_000);
        using ProjectCompilationSession session = new(
            project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        TaskCompletionSource<bool> compiling = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.CompilationChanged += (_, _) =>
        {
            if (session.CompilationState == ProjectCompilationState.Compiling)
            {
                compiling.TrySetResult(true);
            }
        };
        ProjectChangeSet changes = new();
        changes.TrackIds.Add(track.Id);

        _ = session.ApplyEdit(_ => first.Note = 61, changes);
        await compiling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = session.ApplyEdit(_ => second.Note = 62, changes);

        CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);

        Assert.Equal(2, session.SourceRevision);
        Assert.Equal(session.SourceRevision, session.CompiledRevision);
        Assert.Equal(full.Fingerprint, current.Fingerprint);
        Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
    }

    [Fact]
    public async Task EditDoesNotWaitForNonCooperativeMappingExecutionInOlderRevision()
    {
        string markerPath = Path.Combine(
            Path.GetTempPath(),
            $"midora-background-compile-{Guid.NewGuid():N}.marker");
        try
        {
            (MidoraProject project, LogicalTrack _, LogicalNote _, LogicalNote _) =
                CreateNoteProject(2);
            EventInstrument instrument = Assert.Single(project.EventInstruments);
            TemplateEvent templateEvent = Assert.Single(Assert.Single(instrument.SubVoices).Events);
            CSharpMappingFunction function = new(project)
            {
                Name = "blocking-test",
                Body = "return value;"
            };
            instrument.MappingFunctions.Add(function);
            templateEvent.ValueMappings.Add(new ValueMappingStep(project)
            {
                Operation = MappingOperation.CustomCSharp,
                MappingFunctionId = function.Id
            });
            using ProjectCompilationSession session = new(
                project,
                executionMode: ProjectCompilationExecutionMode.Background,
                backgroundDebounce: TimeSpan.Zero);
            ProjectChangeSet changes = new();
            changes.EventInstrumentIds.Add(instrument.Id);
            string escapedMarkerPath = markerPath.Replace("\"", "\"\"");

            _ = session.ApplyEdit(
                _ => function.Body =
                    $"System.IO.File.WriteAllText(@\"{escapedMarkerPath}\", \"started\"); "
                    + "System.Threading.Thread.Sleep(1500); return value;",
                changes);
            await WaitForFileAsync(session, markerPath, TimeSpan.FromSeconds(5));

            Stopwatch editDuration = Stopwatch.StartNew();
            _ = session.ApplyEdit(_ => function.Body = "return value;", changes);
            editDuration.Stop();

            Assert.True(
                editDuration.Elapsed < TimeSpan.FromMilliseconds(500),
                $"The edit waited {editDuration.Elapsed.TotalMilliseconds:F1} ms for an obsolete compiler invocation.");
            CanonicalCompiledResult current = await session.EnsureCurrentCompilationAsync();
            CanonicalCompiledResult full = new MidoraCompiler().CompileFull(project);
            Assert.Equal(full.Fingerprint, current.Fingerprint);
            Assert.Equal(full.Events.ToArray(), current.Events.ToArray());
        }
        finally
        {
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
        }
    }

    private static async Task WaitForFileAsync(
        ProjectCompilationSession session,
        string path,
        TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!File.Exists(path) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5);
        }
        Assert.True(
            File.Exists(path),
            "The background mapping invocation did not start in time. "
                + $"state={session.CompilationState}; "
                + $"diagnostics={string.Join(" | ", session.LastAttempt.Diagnostics.Select(value => value.Message))}");
    }

    private static (MidoraProject Project, LogicalTrack Track, LogicalNote First, LogicalNote Second)
        CreateNoteProject(int noteCount)
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 120,
            RequiresChannelIsolation = true,
            OverlapPolicy = OverlapPolicy.LetOverlap
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentId = instrument.Id
        };
        Segment segment = new(project)
        {
            LengthTicks = noteCount * 120L
        };
        for (int index = 0; index < noteCount; index++)
        {
            segment.Notes.Add(new LogicalNote(project)
            {
                StartTick = index * 120L,
                LengthTicks = 120,
                Note = 60,
                Velocity = 100
            });
        }
        track.Segments.Add(segment);
        project.Tracks.Add(track);
        return (project, track, segment.Notes[0], segment.Notes[1]);
    }
}
