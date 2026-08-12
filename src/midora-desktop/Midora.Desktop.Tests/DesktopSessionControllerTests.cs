using System.Buffers.Binary;
using Midora.Application;
using Midora.AudioRender;
using Midora.AudioDevice.Wave;
using Midora.Compiler;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Midi;
using Midora.Persistence;
using Midora.Playback;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class DesktopSessionControllerTests
{
    [Fact]
    public async Task ProvidedProjectTimelineEditPerformanceProbe()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_TEST_UI_PERF_PROJECT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        await using DesktopSessionController session = new();
        await session.OpenProjectAsync(Path.GetFullPath(path));
        CanonicalCompiledResult independentFull = new MidoraCompiler().CompileFull(session.Project!);
        Assert.Equal(independentFull.Fingerprint, session.Document!.Compilation.LastAttempt.Fingerprint);
        Assert.Equal(
            independentFull.Events.ToArray(),
            session.Document.Compilation.LastAttempt.Events.ToArray());
        _ = session.OpenArrangement();
        Segment[] segments = session.Project!.Tracks.SelectMany(track => track.Segments).ToArray();
        Segment ordinary = segments.OrderBy(segment => segment.Notes.Count).First(segment => segment.Notes.Count > 0);
        Segment extreme = segments.OrderByDescending(segment => segment.Notes.Count).First();
        LogicalNote note = ordinary.Notes[0];
        System.Diagnostics.Stopwatch? activeProbe = null;
        double contentRefreshReachedMs = 0;
        session.Document!.ContentChanged += (_, _) =>
        {
            if (activeProbe is not null) contentRefreshReachedMs = activeProbe.Elapsed.TotalMilliseconds;
        };
        TaskCompletionSource<bool> compilationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Document.Compilation.CompilationChanged += (_, _) =>
        {
            if (session.Document.Compilation.CompilationState == ProjectCompilationState.Compiling)
            {
                compilationStarted.TrySetResult(true);
            }
        };
        var baseEdit = System.Diagnostics.Stopwatch.StartNew();
        activeProbe = baseEdit;
        ProjectEditExecution baseExecution = session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            ordinary.Id,
            [note.Id],
            tickDelta: 1,
            pitchDelta: 0));
        baseEdit.Stop();
        activeProbe = null;
        double baseContentRefreshReachedMs = contentRefreshReachedMs;
        await compilationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var undoDuringCompilation = System.Diagnostics.Stopwatch.StartNew();
        session.Undo();
        undoDuringCompilation.Stop();
        TimelineWorkspaceViewModel extremeWorkspace = session.OpenSegment(extreme.Id);
        TimelineWorkspaceViewModel ordinaryWorkspace = session.OpenSegment(ordinary.Id);

        var refresh = System.Diagnostics.Stopwatch.StartNew();
        session.RefreshWorkspace(extremeWorkspace);
        refresh.Stop();
        var edit = System.Diagnostics.Stopwatch.StartNew();
        activeProbe = edit;
        ProjectEditExecution execution = session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            ordinary.Id,
            [note.Id],
            tickDelta: 1,
            pitchDelta: 0));
        edit.Stop();
        activeProbe = null;
        await session.Document.Compilation.EnsureCurrentCompilationAsync();
        LogicalNote extremeNote = extreme.Notes[0];
        TaskCompletionSource<bool> extremeCompilationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Document.Compilation.CompilationChanged += (_, _) =>
        {
            if (session.Document.Compilation.CompilationState == ProjectCompilationState.Compiling)
            {
                extremeCompilationStarted.TrySetResult(true);
            }
        };
        ProjectEditExecution extremeExecution = session.Execute(
            ProjectDomainEditCommands.MoveLogicalNotes(
                extreme.Id,
                [extremeNote.Id],
                tickDelta: 1,
                pitchDelta: 0));
        await extremeCompilationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(10);
        var extremeUndoDuringCompilation = System.Diagnostics.Stopwatch.StartNew();
        session.Undo();
        extremeUndoDuringCompilation.Stop();
        Console.WriteLine(
            $"[ui-perf] segments={segments.Length}; ordinaryNotes={ordinary.Notes.Count}; "
            + $"extremeNotes={extreme.Notes.Count}; extremeRefreshMs={refresh.Elapsed.TotalMilliseconds:F1}; "
            + $"ordinaryEditArrangementOnlyMs={baseEdit.Elapsed.TotalMilliseconds:F1}; "
            + $"undoWhileCompilingMs={undoDuringCompilation.Elapsed.TotalMilliseconds:F1}; "
            + $"baseContentRefreshReachedMs={baseContentRefreshReachedMs:F1}; "
            + $"ordinaryEditWithAllWorkspaceRefreshMs={edit.Elapsed.TotalMilliseconds:F1}; "
            + $"extremeUndoWhileCompilingMs={extremeUndoDuringCompilation.Elapsed.TotalMilliseconds:F1}; "
            + $"allContentRefreshReachedMs={contentRefreshReachedMs:F1}");

        Assert.True(baseExecution.Changed);
        Assert.True(execution.Changed);
        Assert.True(extremeExecution.Changed);
        Assert.Same(ordinaryWorkspace, session.ActiveWorkspace);
    }

    [Fact]
    public async Task EmbeddedSoundFontBackendFailureDoesNotBlockProjectOpen()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"midora-desktop-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string soundFontPath = Path.Combine(directory, "invalid.sf2");
            await File.WriteAllBytesAsync(soundFontPath, [1, 2, 3, 4]);
            MidoraProject project = new(192);
            await using EmbeddedSoundFontResourceV1 resource =
                await SoundFontBindingV1.BindEmbeddedAsync(project, soundFontPath);
            string projectPath = Path.Combine(directory, "embedded.midora");
            _ = await new MidoraProjectPackageV1("0.1.0-dev").SaveProjectAsync(
                project,
                projectPath,
                embeddedSoundFontResource: resource);

            await using DesktopSessionController session = new();
            await session.OpenProjectAsync(projectPath);

            Assert.True(session.HasProject);
            Assert.IsType<EmbeddedProjectSoundFontReference>(session.Project!.SoundFont.Reference);
            Assert.False(session.CanPreview);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReopenedEmbeddedProjectRestoresVerifiedSoundFontAndPlaysTwice()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_TEST_EMBEDDED_PROJECT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        await using DesktopSessionController session = new();
        await session.OpenProjectAsync(Path.GetFullPath(path));

        Assert.IsType<EmbeddedProjectSoundFontReference>(session.Project!.SoundFont.Reference);
        Assert.True(session.CanPreview, session.PlaybackUnavailableReason);
        Assert.True(session.CanPlayback, session.PlaybackUnavailableReason);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            session.StartPlayback();
            long deadline = Environment.TickCount64 + 10_000;
            while (session.IsPlaybackActive && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(2);
                session.UpdatePlayback();
            }

            Assert.True(
                session.PlaybackState == Midora.Playback.PlaybackState.Stopped,
                $"Attempt={attempt}; state={session.PlaybackState}; notice={session.Notice}");
            Assert.Null(session.Notice);
        }

        EventInstrument instrument = Assert.Single(session.Project.EventInstruments);
        MidoraId subVoiceId = Assert.Single(instrument.SubVoices).Id;
        (LogicalTrack Track, Segment Segment) segmentContext = session.Project.Tracks
            .Where(track => track.EventInstrumentId == instrument.Id)
            .SelectMany(track => track.Segments.Select(segment => (Track: track, Segment: segment)))
            .First();
        (string Name, Action Start)[] previewPaths =
        [
            ("Event Instrument", () => session.StartHeldEventInstrumentPreview(
                new EventInstrumentPreviewRequest(
                    instrument.Id,
                    Pitch: 60,
                    Tempo: 120m))),
            ("SubVoice", () => session.StartHeldEventInstrumentPreview(
                new EventInstrumentPreviewRequest(
                    instrument.Id,
                    subVoiceId,
                    Pitch: 60,
                    Tempo: 120m))),
            ("Segment Pitch Ruler", () => session.StartHeldSegmentPitchRulerPreview(
                segmentContext.Track.Id,
                segmentContext.Segment.Id,
                pitch: 60,
                velocity: 100,
                tempo: 120m))
        ];
        foreach ((string previewPath, Action startPreview) in previewPaths)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                startPreview();
                Thread.Sleep(50);
                session.EndHeldPreviewGate();
                long deadline = Environment.TickCount64 + 10_000;
                while (session.IsPlaybackActive && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(2);
                    session.UpdatePlayback();
                }

                Assert.True(
                    session.PlaybackState == Midora.Playback.PlaybackState.Stopped,
                    $"Preview={previewPath}; attempt={attempt}; state={session.PlaybackState}; notice={session.Notice}");
                Assert.Null(session.Notice);
            }
        }
    }

    [Fact]
    public async Task ReopenedEmbeddedProjectRendersWholeMixAndCleansEverySegmentBoundary()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_TEST_EMBEDDED_PROJECT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"midora-desktop-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            await using DesktopSessionController session = new();
            await session.OpenProjectAsync(Path.GetFullPath(path));

            Segment[] segments = session.Project!.Tracks
                .SelectMany(track => track.Segments)
                .OrderBy(segment => segment.ProjectStartTick)
                .ToArray();
            Assert.True(
                segments.Length >= 2,
                $"Expected at least two Segments, found {segments.Length}.");

            CanonicalCompiledResult compiled = await session.CompileProjectAsync();
            Assert.True(compiled.IsConsumable, string.Join(
                Environment.NewLine,
                compiled.Diagnostics.Select(value => $"{value.Severity} {value.Code}: {value.Message}")));
            foreach (Segment segment in segments)
            {
                bool hasSoundingOutput = compiled.Events.ToArray().Any(value =>
                    value.Source.SegmentId == segment.Id
                    && value.Message.MessageType == MidiMessageType.NoteOn
                    && value.Message.Byte2 != 0);
                if (!hasSoundingOutput)
                {
                    continue;
                }
                long boundary = checked(segment.ProjectStartTick + segment.LengthTicks);
                CanonicalMidiEvent[] allEvents = compiled.Events.ToArray();
                Assert.True(allEvents.Any(value =>
                    value.Tick >= segment.ProjectStartTick
                    && value.Tick <= boundary
                    && (value.Source.SegmentId == segment.Id
                        || (value.Tick == compiled.Context.EndTick
                            && value.Source.Origin == SourceOrigin.CompilerBoundaryCleanup))
                    && value.Message.MessageType == MidiMessageType.ControlChange
                    && value.Message.Byte1 == 120
                    && value.Message.Byte2 == 0
                    && value.Role == CanonicalEventRole.Reset),
                    $"Segment={segment.Id.Value}; range=[{segment.ProjectStartTick},{boundary}]; resets={string.Join(", ", allEvents.Where(value => value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 120).Select(value => $"{value.Tick}/seg={value.Source.SegmentId.Value}/role={value.Role}"))}");
            }

            string outputPath = Path.Combine(outputDirectory, "exact-project.wav");
            await using PreparedDesktopAudioRender prepared = await session.PrepareAudioRenderAsync(new(
                AudioRenderMode.WholeMix,
                outputPath,
                StartTick: 0,
                EndTick: null,
                SampleRate: 48_000,
                MaximumSampleVoicesPerUnitStream: 500,
                TreatWarningsAsErrors: false,
                AcceptExternalSoundFontHashChange: false));
            Assert.True(prepared.Succeeded, FormatPreparation(prepared));

            AudioRenderTaskResult result = await session.ExecuteAudioRenderAsync(
                prepared,
                overwriteAuthorized: false);
            Assert.True(result.Status == AudioRenderTaskStatus.Completed, FormatResult(result));
            Assert.True(File.Exists(outputPath), FormatResult(result));

            (Segment Left, Segment Right)[] separatedPairs = segments.Zip(segments.Skip(1))
                .Where(pair => pair.First.ProjectStartTick + pair.First.LengthTicks
                    < pair.Second.ProjectStartTick)
                .Select(pair => (pair.First, pair.Second))
                .ToArray();
            Assert.NotEmpty(separatedPairs);
            TempoSampleMap tempoMap = new(compiled.TicksPerQuarterNote, compiled.Tempos);
            foreach ((Segment left, Segment right) in separatedPairs)
            {
                long boundaryFrame = tempoMap.TickToSampleFrame(
                    left.ProjectStartTick + left.LengthTicks,
                    compiled.StartTick,
                    48_000);
                long nextFrame = tempoMap.TickToSampleFrame(
                    right.ProjectStartTick,
                    compiled.StartTick,
                    48_000);
                Assert.Equal(0f, ReadPeak(outputPath, boundaryFrame, nextFrame));
            }
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        static string FormatPreparation(PreparedDesktopAudioRender value) => string.Join(
            Environment.NewLine,
            value.Compilation.Diagnostics.Select(item => $"{item.Severity} {item.Code}: {item.Message}")
                .Concat(value.OutputPlan.Diagnostics.Select(item =>
                    $"{item.Severity} {item.Code}: {item.Message}")));

        static string FormatResult(AudioRenderTaskResult value) => string.Join(
            Environment.NewLine,
            value.Diagnostics.Select(item => $"task {item.Severity} {item.Code}: {item.Message}")
                .Concat(value.Outputs.SelectMany(output => output.CompilerDiagnostics.Select(item =>
                    $"{output.Target.FullPath}: compiler {item.Severity} {item.Code}: {item.Message}")))
                .Concat(value.Outputs.SelectMany(output => output.Diagnostics.Select(item =>
                    $"{output.Target.FullPath}: {item.Severity} {item.Code}: {item.Message}"))));

        static float ReadPeak(string path, long startFrame, long endFrame)
        {
            byte[] bytes = File.ReadAllBytes(path);
            float peak = 0;
            for (long frame = startFrame; frame < endFrame; frame++)
            {
                int offset = checked(WaveFileSize.HeaderByteCount + (int)(frame * 8));
                for (int channelOffset = 0; channelOffset < 8; channelOffset += 4)
                {
                    float sample = BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + channelOffset, 4)));
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
            return peak;
        }
    }

    [Fact]
    public async Task UnsavedProjectReportsUnsavedAndOpensArrangement()
    {
        await using DesktopSessionController session = new();

        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });

        Assert.True(session.HasProject);
        Assert.Equal("Untitled Project", session.ProjectDisplayName);
        Assert.Equal("Unsaved", session.ProjectState);
        Assert.Null(session.Persistence?.CurrentProjectPath);
        Assert.True(session.CanSaveProject);
        Assert.Equal(5, session.ProjectTree.Count);
        Assert.Equal(WorkspaceKind.Arrangement, session.ActiveWorkspace?.Kind);
    }

    [Fact]
    public async Task ProjectTreeFilterSearchesOnlySpecifiedObjectFieldsAndKeepsAncestors()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Filter",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Strings"));
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Lead"));

        session.ProjectTreeSearchText = "string";

        ProjectTreeNode library = Assert.Single(session.ProjectTree);
        Assert.Equal(ProjectTreeNodeKind.InstrumentLibrary, library.Kind);
        Assert.Equal("Strings", Assert.Single(library.Children).Title);

        session.ProjectTreeSearchText = "lead";
        ProjectTreeNode tracks = Assert.Single(session.ProjectTree);
        Assert.Equal(ProjectTreeNodeKind.LogicalTracks, tracks.Kind);
        Assert.Equal("Lead", Assert.Single(tracks.Children).Title);

        session.ProjectTreeSearchText = "diagnostic";
        Assert.Empty(session.ProjectTree);
    }

    [Fact]
    public async Task NavigationHistoryAndTaskLockLevelsAreIndependent()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Navigation",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        WorkspaceViewModel arrangement = session.ActiveWorkspace!;
        ProjectTreeNode diagnosticsNode = session.ProjectTree.Single(
            item => item.Kind == ProjectTreeNodeKind.Diagnostics);
        WorkspaceViewModel diagnostics = session.OpenWorkspace(diagnosticsNode);

        Assert.True(session.CanNavigateBack);
        session.NavigateBack();
        Assert.Same(arrangement, session.ActiveWorkspace);
        Assert.True(session.CanNavigateForward);
        session.NavigateForward();
        Assert.Same(diagnostics, session.ActiveWorkspace);

        DesktopTaskViewModel compile = session.BeginTask(
            "Compile",
            canCancel: true,
            DesktopTaskLockLevel.ProjectEdit);
        Assert.False(session.IsMainWindowTaskLocked);
        Assert.False(session.CanEditProject);
        Assert.True(session.CanUseContextMenus);
        session.CompleteTask(compile, "Succeeded");

        DesktopTaskViewModel render = session.BeginTask(
            "Render",
            canCancel: true,
            DesktopTaskLockLevel.FullApplication);
        Assert.True(session.IsMainWindowTaskLocked);
        Assert.True(session.IsFullApplicationTaskLocked);
        Assert.False(session.CanUseContextMenus);
        render.SetCancellationAvailable(false);
        Assert.False(render.CanRequestCancel);
        session.CompleteTask(render, "Succeeded");
    }

    [Fact]
    public async Task WorkspaceReorderChangesSessionOrderWithoutChangingProjectHistory()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Workspace Order",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        WorkspaceViewModel arrangement = session.ActiveWorkspace!;
        WorkspaceViewModel diagnostics = session.OpenWorkspace(session.ProjectTree.Single(
            item => item.Kind == ProjectTreeNodeKind.Diagnostics));
        WorkspaceViewModel settings = session.OpenWorkspace(session.ProjectTree.Single(
            item => item.Kind == ProjectTreeNodeKind.ProjectSettings));
        int historyCount = session.Document!.History.Count;

        session.ReorderWorkspace(settings, 0);

        Assert.Equal([settings, arrangement, diagnostics], session.Workspaces);
        Assert.Same(settings, session.ActiveWorkspace);
        Assert.Equal(historyCount, session.Document.History.Count);
    }

    [Fact]
    public async Task MultiNoteInspectorDistinguishesMixedAndAppliesOneExactSetEdit()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Inspector",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 0, 60, 60, 100));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 120, 90, 64, 80));
        LogicalNote first = segment.Notes[0];
        LogicalNote second = segment.Notes[1];
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Add(first.Id, makePrimary: false);
        workspace.Selection.Add(second.Id);
        session.RefreshWorkspace(workspace);

        InspectorField length = session.Inspector.Fields.Single(item => item.Key == "batch.note.length");
        InspectorField velocity = session.Inspector.Fields.Single(item => item.Key == "batch.note.velocity");
        Assert.True(length.IsMixed);
        Assert.True(velocity.IsMixed);
        Assert.Equal("Mixed", velocity.Value);

        int historyCount = session.Document!.History.Count;
        velocity.Value = "72";
        session.ApplyInspectorField(velocity);

        Assert.Equal(72, first.Velocity);
        Assert.Equal(72, second.Velocity);
        Assert.Equal(historyCount + 1, session.Document.History.Count);
        session.Document.Undo();
        Assert.Equal((100, 80), (first.Velocity, second.Velocity));
    }

    [Fact]
    public async Task TimeRangeAndObjectSelectionConvertExplicitlyAndRemainIndependent()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Time Range",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 240, 480));
        Segment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel workspace = Assert.IsType<TimelineWorkspaceViewModel>(session.ActiveWorkspace);
        workspace.Selection.Replace(segment.Id);
        session.RefreshWorkspace(workspace);

        Assert.True(workspace.SetTimeRangeFromObjectSelection());
        Assert.Equal(240, workspace.TimeRangeStartTick);
        Assert.Equal(720, workspace.TimeRangeEndTick);

        workspace.Selection.Clear();
        Assert.Equal(240, workspace.TimeRangeStartTick);
        Assert.True(workspace.SetObjectSelectionFromTimeRange());
        Assert.Equal(segment.Id, Assert.Single(workspace.Selection.Ids));

        workspace.ClearTimeRange();
        Assert.False(workspace.HasTimeRange);
        Assert.Equal(segment.Id, Assert.Single(workspace.Selection.Ids));
    }

    [Fact]
    public async Task TimelineRefreshPreservesUserZoomAndPrunesNonInteractiveArrangementPreviewIds()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Timeline state",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 0, 120, 60, 100));
        LogicalNote note = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        arrangement.TickSpan = 96;
        arrangement.Selection.Replace(note.Id);

        session.RefreshWorkspace(arrangement);

        Assert.Equal(96, arrangement.TickSpan);
        Assert.Empty(arrangement.Selection.Ids);
    }

    [Fact]
    public async Task ArrangementReusesUnchangedSegmentPreviewAndRebuildsChangedSegmentOnly()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Preview cache",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 0, 120, 60, 100));
        LogicalNote note = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        TimelineSegmentPreview first = arrangement.Snapshot!.SegmentPreviews[segment.Id];

        session.RefreshWorkspace(arrangement);
        TimelineSegmentPreview unchanged = arrangement.Snapshot!.SegmentPreviews[segment.Id];
        session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(segment.Id, [note.Id], 24, 1));
        TimelineSegmentPreview changed = arrangement.Snapshot!.SegmentPreviews[segment.Id];

        Assert.Same(first, unchanged);
        Assert.NotSame(first, changed);
        Assert.Equal(0.05, changed.Notes[0].NormalizedStart, precision: 10);
        Assert.Equal(61, changed.Notes[0].Pitch);
    }

    [Fact]
    public async Task SelectionOnlyRefreshDoesNotRebuildTimelineSnapshotOrIntervalIndex()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Selection overlay",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        TimelineRenderSnapshot snapshot = arrangement.Snapshot!;
        TimelineIntervalIndex index = snapshot.Index;

        arrangement.Selection.Replace(segment.Id);
        session.RefreshWorkspaceSelection(arrangement);

        Assert.Same(snapshot, arrangement.Snapshot);
        Assert.Same(index, arrangement.Snapshot!.Index);
        Assert.True(arrangement.SelectionSnapshot.Contains(segment.Id));
        Assert.Equal(segment.Id, arrangement.SelectionSnapshot.Primary);
    }

    [Fact]
    public async Task TrackEditDoesNotRebuildUnrelatedOpenSegmentWorkspace()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Targeted workspace refresh",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Edited"));
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Unrelated"));
        LogicalTrack editedTrack = session.Project!.Tracks[0];
        LogicalTrack unrelatedTrack = session.Project.Tracks[1];
        session.Execute(ProjectDomainEditCommands.CreateSegment(editedTrack.Id, 0, 480));
        session.Execute(ProjectDomainEditCommands.CreateSegment(unrelatedTrack.Id, 0, 480));
        Segment editedSegment = editedTrack.Segments[0];
        Segment unrelatedSegment = unrelatedTrack.Segments[0];
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(editedSegment.Id, 0, 120, 60, 100));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(unrelatedSegment.Id, 0, 120, 64, 100));
        TimelineWorkspaceViewModel unrelatedWorkspace = session.OpenSegment(unrelatedSegment.Id);
        TimelineRenderSnapshot before = unrelatedWorkspace.Snapshot!;
        LogicalNote editedNote = editedSegment.Notes[0];

        session.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            editedSegment.Id,
            [editedNote.Id],
            24,
            0));

        Assert.Same(before, unrelatedWorkspace.Snapshot);
    }

    [Fact]
    public async Task SegmentTimelineMapsLocalCursorAndRangeToProjectCoordinates()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Segment coordinates",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(
            track.Id,
            projectStartTick: 960,
            lengthTicks: 480,
            contentOffsetTick: 240));
        Segment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        Assert.Equal(960, workspace.ToProjectTick(session.Project, 240));
        Assert.Equal(1_020, workspace.ToProjectTick(session.Project, 300));
        Assert.Equal(new TickRange(1_020, 1_140), workspace.ToProjectRange(session.Project, 300, 420));

        workspace.UpdatePlaybackCursor(session.Project, 1_020);
        Assert.Equal(300, workspace.PlaybackCursorTick);
        workspace.UpdatePlaybackCursor(session.Project, 100);
        Assert.Null(workspace.PlaybackCursorTick);
    }

    [Fact]
    public async Task SegmentTimelineDimsNotesOnlyWhenGateStartIsOutsideActiveRange()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Segment note range state",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track"));
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(
            track.Id,
            projectStartTick: 0,
            lengthTicks: 480,
            contentOffsetTick: 240));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 200, 80, 60, 100));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 700, 40, 62, 100));
        LogicalNote outsideStart = segment.Notes.Single(note => note.StartTick == 200);
        LogicalNote insideStartWithOutsideEnd = segment.Notes.Single(note => note.StartTick == 700);

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        TimelineRenderItem outsideItem = workspace.Snapshot!.Items.Single(item => item.Id == outsideStart.Id);
        TimelineRenderItem crossingEndItem = workspace.Snapshot.Items.Single(item => item.Id == insideStartWithOutsideEnd.Id);

        Assert.True(outsideItem.State.HasFlag(TimelineItemState.OutsideActiveRange));
        Assert.False(crossingEndItem.State.HasFlag(TimelineItemState.OutsideActiveRange));
    }

    [Fact]
    public async Task ArrangementBuildsReadOnlyConductorOverviewForItsRuler()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Conductor Overview",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateTempo(96, 132.5m));
        session.Execute(ProjectDomainEditCommands.CreateTimeSignature(192, 3, 4));
        session.Execute(ProjectDomainEditCommands.CreateProjectMarker(384, "Verse"));

        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();

        Assert.NotNull(arrangement.RulerSnapshot);
        Assert.True(arrangement.RulerSnapshot.Items.Count >= 3);
        Assert.All(arrangement.RulerSnapshot.Items, item =>
            Assert.True(item.State.HasFlag(TimelineItemState.HitTestDisabled)));
        Assert.Contains(arrangement.RulerSnapshot.Items, item => item.Label.Contains("132.5 BPM", StringComparison.Ordinal));
        Assert.Contains(arrangement.RulerSnapshot.Items, item => item.Label == "3/4");
        Assert.Contains(arrangement.RulerSnapshot.Items, item => item.Label == "Verse");
    }

    [Fact]
    public void DiagnosticPanelsKeepIndependentFilterStateOverSharedIdentities()
    {
        DiagnosticRow activeError = new(
            "Error", "Compile", "CMP-1", "Failure", "Project", true, default);
        DiagnosticRow resolvedWarning = new(
            "Warning", "Compile", "CMP-2", "Old warning", "Project", false, default);
        DiagnosticRow[] shared = [activeError, resolvedWarning];
        DiagnosticsWorkspaceViewModel full = new();
        DiagnosticsWorkspaceViewModel compact = new();
        full.Replace(shared);
        compact.Replace(shared);

        full.StatusFilter = "All statuses";
        full.SeverityFilter = "Warning";
        compact.StatusFilter = "Active";
        compact.SeverityFilter = "Error";

        Assert.Same(resolvedWarning, Assert.Single(full.Diagnostics));
        Assert.Same(activeError, Assert.Single(compact.Diagnostics));
        Assert.Equal("Warning", full.SeverityFilter);
        Assert.Equal("Error", compact.SeverityFilter);
    }

    [Fact]
    public void StatusMessageRetainsFullErrorTextForDetails()
    {
        DesktopSessionController session = new();
        string message = "Playback failed:\nworker state=Faulted\nfull diagnostic payload";

        session.SetStatusMessage(message, isError: true);

        Assert.Equal(message, session.StatusMessage);
        Assert.True(session.HasStatusMessage);
        Assert.True(session.StatusMessageIsError);
    }

    [Fact]
    public async Task SubVoiceEditorSeparatesRenderedNotesFromEventLanesAndInitialState()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "SubVoice Editor",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Strings"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Attack", rootNoteOverride: 62));
        SubVoice voice = instrument.SubVoices.Single(item => item.Name == "Attack");
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(instrument.Id, voice.Id, 48, 96, 64, 80));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(instrument.Id, voice.Id, 72, 1, 96));
        session.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
            instrument.Id,
            voice.Id,
            new MidiValueTarget(MidiValueKind.ControlChange, 7),
            100));

        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Replace(voice.Id);
        session.RefreshWorkspace(workspace);

        TimelineRenderItem note = Assert.Single(workspace.SubVoiceNoteSnapshot!.Items);
        Assert.Equal(TimelineItemKind.TemplateNote, note.Kind);
        Assert.Equal(63, note.Lane);
        Assert.Equal(80 / 127d, note.Value, 10);
        TimelineRenderItem midiEvent = Assert.Single(workspace.SubVoiceEventSnapshot!.Items);
        Assert.Equal(TimelineItemKind.TemplateEvent, midiEvent.Kind);
        Assert.DoesNotContain(workspace.SubVoiceEventSnapshot.Items, item => item.Kind == TimelineItemKind.TemplateNote);
        Assert.Equal(voice.Id, workspace.ActiveSubVoiceId);
        Assert.Contains("override", workspace.ActiveSubVoiceContext, StringComparison.Ordinal);
        Assert.Contains(workspace.InitialStateEntries, item => item.Target == "CC 7" && item.Value == "100");
    }

}
