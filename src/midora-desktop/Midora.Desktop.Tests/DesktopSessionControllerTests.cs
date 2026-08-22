using System.Buffers.Binary;
using System.Collections.Specialized;
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

            string extractedPath = Path.Combine(directory, "extracted.sf2");
            await session.ExtractEmbeddedSoundFontAsync(
                extractedPath,
                overwriteAuthorized: false);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(extractedPath));
            await Assert.ThrowsAsync<IOException>(() => session.ExtractEmbeddedSoundFontAsync(
                extractedPath,
                overwriteAuthorized: false));
            await File.WriteAllBytesAsync(extractedPath, [9]);
            await session.ExtractEmbeddedSoundFontAsync(
                extractedPath,
                overwriteAuthorized: true);
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(extractedPath));
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
            .Where(track => session.Project.FindEventInstrumentDefinition(track)?.Id == instrument.Id)
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
        HashSet<string?> notifications = [];
        session.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

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
        Assert.Contains(nameof(session.CanPlayback), notifications);
        Assert.Contains(nameof(session.CanTogglePlayback), notifications);
        Assert.Contains(nameof(session.PrimaryTransportAction), notifications);
        Assert.Contains(nameof(session.PrimaryTransportToolTip), notifications);
    }

    [Fact]
    public async Task CompilationCompletionRefreshesProjectTreeDiagnosticCountsWithoutLag()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Diagnostic counts",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateTimeSignature(100, 4, 4));
        TimeSignatureChange change = session.Project!.Conductor.TimeSignatures.Single(item => item.Tick == 100);
        await session.Document!.Compilation.EnsureCurrentCompilationAsync();
        await WaitUntilAsync(() =>
            (session.WarningCount > 0 || session.ErrorCount > 0)
            && session.ProjectTree.Single(item => item.Kind == ProjectTreeNodeKind.Diagnostics).Title
                == $"Diagnostics ({session.IssueSummary})");

        ProjectTreeNode failedNode = session.ProjectTree.Single(item => item.Kind == ProjectTreeNodeKind.Diagnostics);
        Assert.Equal($"Diagnostics ({session.IssueSummary})", failedNode.Title);
        Assert.True(session.WarningCount > 0 || session.ErrorCount > 0);

        session.Execute(ProjectDomainEditCommands.DeleteTimeSignature(change.Id));
        await session.Document.Compilation.EnsureCurrentCompilationAsync();
        await WaitUntilAsync(() =>
            session.WarningCount == 0
            && session.ErrorCount == 0
            && session.ProjectTree.Single(item => item.Kind == ProjectTreeNodeKind.Diagnostics).Title
                == $"Diagnostics ({session.IssueSummary})");

        ProjectTreeNode repairedNode = session.ProjectTree.Single(item => item.Kind == ProjectTreeNodeKind.Diagnostics);
        Assert.Equal($"Diagnostics ({session.IssueSummary})", repairedNode.Title);
        Assert.NotEqual(failedNode.Title, repairedNode.Title);
    }

    [Fact]
    public async Task FlatArrangementUsesOneQuarterDefaultAndShowsBoundInstrumentSubtitle()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Arrangement headers",
            TicksPerQuarterNote = 480,
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Layered Strings"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Lead", instrument.Id));

        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();

        Assert.Equal(480, arrangement.EditorSettings.DefaultLengthTicks);
        TimelineRenderSnapshot snapshot = Assert.IsType<TimelineRenderSnapshot>(arrangement.Snapshot);
        Assert.Equal(["Conductor", "Lead"], snapshot.LaneLabels);
        ArrangementLaneDescriptor logicalTrackLane = Assert.Single(snapshot.ArrangementLanes, value =>
            value.Kind == ArrangementLaneKind.LogicalTrack);
        Assert.Equal("Layered Strings", snapshot.LaneSecondaryLabels[logicalTrackLane.Lane]);
    }

    [Fact]
    public async Task PositionTickFieldUsesTheProjectTpqDigitCount()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Wide TPQ",
            TicksPerQuarterNote = 1_920,
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });

        Assert.EndsWith(" : 0000", session.PositionText, StringComparison.Ordinal);
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
        CreateLogicalTrack(session, "Lead");

        session.ProjectTreeSearchText = "string";

        Assert.Equal(2, session.ProjectTree.Count);
        ProjectTreeNode library = session.ProjectTree.Single(value =>
            value.Kind == ProjectTreeNodeKind.InstrumentLibrary);
        Assert.Equal(ProjectTreeNodeKind.InstrumentLibrary, library.Kind);
        Assert.Equal("Strings", Assert.Single(library.Children).Title);
        ProjectTreeNode matchingTracks = session.ProjectTree.Single(value =>
            value.Kind == ProjectTreeNodeKind.LogicalTracks);
        Assert.Equal("Lead", Assert.Single(matchingTracks.Children).Title);

        session.ProjectTreeSearchText = "lead";
        ProjectTreeNode tracks = Assert.Single(session.ProjectTree);
        Assert.Equal(ProjectTreeNodeKind.LogicalTracks, tracks.Kind);
        Assert.Equal("Lead", Assert.Single(tracks.Children).Title);

        session.ProjectTreeSearchText = "diagnostic";
        Assert.Empty(session.ProjectTree);
    }

    [Fact]
    public async Task SelectionTransformUndoRestoresIdsOfNotesDeletedByTheTransform()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Selection history",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 0, 120, 60, 100));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 120, 120, 70, 100));
        LogicalNote retained = segment.Notes[0];
        LogicalNote discarded = segment.Notes[1];
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Add(retained.Id, makePrimary: false);
        workspace.Selection.Add(discarded.Id, makePrimary: true);
        session.RefreshWorkspaceSelection(workspace);

        session.ExecutePreservingWorkspaceSelection(
            ProjectDomainEditCommands.TransposeLogicalNotes(
                segment.Id,
                [retained.Id, discarded.Id],
                semitones: 64),
            workspace);

        Assert.Equal([retained.Id], workspace.Selection.Ids);
        session.Undo();
        Assert.Equal([retained.Id, discarded.Id], workspace.Selection.Ids);
        Assert.Equal(discarded.Id, workspace.Selection.Primary);
        session.Redo();
        Assert.Equal([retained.Id], workspace.Selection.Ids);
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

        Assert.Equal([arrangement, settings, diagnostics], session.Workspaces);
        Assert.Same(settings, session.ActiveWorkspace);
        Assert.Equal(historyCount, session.Document.History.Count);

        session.ReorderWorkspace(arrangement, 2);
        session.CloseWorkspace(arrangement);

        Assert.Equal([arrangement, settings, diagnostics], session.Workspaces);
        Assert.False(arrangement.CanClose);
        Assert.False(arrangement.CanReorder);
    }

    [Fact]
    public async Task MultiNotePropertiesDistinguishMixedAndApplyOneExactSetEdit()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Object Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
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

        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        Assert.True(ObjectPropertiesProjection.CanEditInPropertiesDialog(workspace, properties));
        PropertyField length = properties.Fields.Single(item => item.Key == "batch.note.length");
        PropertyField velocity = properties.Fields.Single(item => item.Key == "batch.note.velocity");
        Assert.True(length.IsMixed);
        Assert.True(velocity.IsMixed);
        Assert.Equal("Mixed", velocity.Value);

        int historyCount = session.Document!.History.Count;
        velocity.ActivateMixedEdit();
        velocity.Value = "72";
        Assert.True(velocity.CanReset);
        velocity.Reset();
        Assert.True(velocity.IsMixed);
        Assert.Equal("Mixed", velocity.Value);
        velocity.ActivateMixedEdit();
        velocity.Value = "72";
        session.ApplyObjectProperties(workspace, [velocity]);

        Assert.Equal(72, first.Velocity);
        Assert.Equal(72, second.Velocity);
        Assert.Equal(historyCount + 1, session.Document.History.Count);
        session.Document.Undo();
        Assert.Equal((100, 80), (first.Velocity, second.Velocity));
    }

    [Fact]
    public async Task DirectMidiEventPropertiesUseMusicalFieldsAndApplyAtomically()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Direct MIDI Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        PureMidiTrack track = Assert.Single(session.Project!.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
        MidiSegment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            24,
            DirectMidiChannelEventKind.ControlChange,
            11,
            32));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            72,
            DirectMidiChannelEventKind.ControlChange,
            74,
            96));
        DirectMidiChannelEvent first = segment.ChannelEvents[0];
        DirectMidiChannelEvent second = segment.ChannelEvents[1];
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Add(first.Id, makePrimary: false);
        workspace.Selection.Add(second.Id);
        session.RefreshWorkspace(workspace);

        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        PropertyField type = properties.Fields.Single(item => item.Key == "batch.midiEvent.kind");
        PropertyField controller = properties.Fields.Single(
            item => item.Key == "batch.midiEvent.controller");
        PropertyField eventValue = properties.Fields.Single(
            item => item.Key == "batch.midiEvent.value");
        Assert.False(type.IsEditable);
        Assert.Equal("EVENT TYPE", type.Label);
        Assert.True(controller.IsMixed);
        Assert.True(eventValue.IsMixed);
        Assert.DoesNotContain(properties.Fields, item => item.Label is "DATA 1" or "DATA 2");

        int historyCount = session.Document!.History.Count;
        controller.ActivateMixedEdit();
        controller.Value = "7";
        eventValue.ActivateMixedEdit();
        eventValue.Value = "100";
        session.ApplyObjectProperties(workspace, [controller, eventValue]);

        Assert.All(segment.ChannelEvents, item =>
        {
            Assert.Equal(7, item.Data1);
            Assert.Equal(100, item.Data2);
        });
        Assert.Equal(historyCount + 1, session.Document.History.Count);
        session.Document.Undo();
        Assert.Equal((11, 32), (first.Data1, first.Data2));
        Assert.Equal((74, 96), (second.Data1, second.Data2));
    }

    [Fact]
    public async Task MultiFieldPropertiesValidateOnlyTheFinalSegmentAndNoteState()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Final Properties State",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 100));
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 150, 100));
        Segment editedSegment = track.Segments[0];
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        arrangement.Selection.Replace(editedSegment.Id);
        session.RefreshWorkspace(arrangement);

        ObjectPropertiesViewModel segmentProperties = session.CreateObjectProperties(arrangement);
        PropertyField segmentStart = segmentProperties.Fields.Single(value =>
            value.Key == "segment.start");
        PropertyField segmentLength = segmentProperties.Fields.Single(value =>
            value.Key == "segment.length");
        segmentStart.Value = "100";
        segmentLength.Value = "50";
        session.ApplyObjectProperties(arrangement, [segmentStart, segmentLength]);

        Assert.Equal((100L, 50L), (
            editedSegment.ProjectStartTick,
            editedSegment.LengthTicks));

        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            editedSegment.Id,
            startTick: 10,
            lengthTicks: 10,
            note: 60,
            velocity: 90));
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            editedSegment.Id,
            startTick: 20,
            lengthTicks: 10,
            note: 60,
            velocity: 100));
        LogicalNote incumbent = editedSegment.Notes[0];
        LogicalNote moved = editedSegment.Notes[1];
        TimelineWorkspaceViewModel editor = session.OpenSegment(editedSegment.Id);
        editor.Selection.Replace(moved.Id);
        session.RefreshWorkspace(editor);
        ObjectPropertiesViewModel noteProperties = session.CreateObjectProperties(editor);
        PropertyField noteStart = noteProperties.Fields.Single(value => value.Key == "note.start");
        PropertyField noteNumber = noteProperties.Fields.Single(value => value.Key == "note.number");
        noteStart.Value = "10";
        noteNumber.Value = "61";
        session.ApplyObjectProperties(editor, [noteStart, noteNumber]);

        Assert.Equal(2, editedSegment.Notes.Count);
        Assert.Contains(incumbent, editedSegment.Notes);
        Assert.Contains(moved, editedSegment.Notes);
        Assert.Equal((10L, 61), (moved.StartTick, moved.Note));
    }

    [Fact]
    public async Task DirectMidiEventPropertiesResolveCollisionsFromFinalRouteOnly()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Direct MIDI Final Properties State",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        PureMidiTrack track = Assert.Single(session.Project!.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
        MidiSegment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            0,
            DirectMidiChannelEventKind.ControlChange,
            7,
            32));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            10,
            DirectMidiChannelEventKind.ControlChange,
            7,
            96));
        DirectMidiChannelEvent incumbent = segment.ChannelEvents[0];
        DirectMidiChannelEvent moved = segment.ChannelEvents[1];
        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        workspace.Selection.Replace(moved.Id);
        session.RefreshWorkspace(workspace);

        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        PropertyField tick = properties.Fields.Single(value => value.Key == "midiEvent.tick");
        PropertyField controller = properties.Fields.Single(value =>
            value.Key == "midiEvent.controller");
        tick.Value = "0";
        controller.Value = "74";
        session.ApplyObjectProperties(workspace, [tick, controller]);

        Assert.Equal(2, segment.ChannelEvents.Count);
        Assert.Contains(incumbent, segment.ChannelEvents);
        Assert.Contains(moved, segment.ChannelEvents);
        Assert.Equal((0L, 74), (moved.Tick, moved.Data1));
    }

    [Fact]
    public async Task MappingStepPropertiesChangeSourceAndReferenceInOneTransaction()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Mapping Step Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Expression",
            LogicalParameterType.Double,
            0,
            1,
            0,
            1,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterMapping(
            instrument.Id,
            parameter.Id,
            voice.Id,
            MidiValueTarget.ControlChange(11)));
        LogicalParameterMapping mapping = Assert.Single(instrument.ParameterMappings);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Brightness",
            LogicalParameterType.Double,
            0,
            1,
            0,
            1,
            0));
        LogicalParameterDefinition secondParameter = instrument.LogicalParameters.Single(value =>
            value.Name == "Brightness");
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterMapping(
            instrument.Id,
            secondParameter.Id,
            voice.Id,
            MidiValueTarget.ControlChange(74)));
        LogicalParameterMapping secondMapping = instrument.ParameterMappings.Single(value =>
            value.ParameterId == secondParameter.Id);
        session.Execute(ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            mapping.Steps.Id,
            MappingSource.Constant,
            MappingOperation.Override,
            constant: 0.5));
        ValueMappingStep step = Assert.Single(mapping.Steps);
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Replace(step.Id);
        session.RefreshWorkspace(workspace);

        ObjectPropertiesViewModel properties = session.CreateObjectProperties(workspace);
        PropertyField owner = properties.Fields.Single(value =>
            value.Key == "mappingStep.chain");
        PropertyField source = properties.Fields.Single(value =>
            value.Key == "mappingStep.source");
        PropertyField parameterReference = properties.Fields.Single(value =>
            value.Key == "mappingStep.logicalParameter");
        Assert.True(parameterReference.IsChoice);
        Assert.Contains(parameterReference.Choices, value =>
            value.Value == parameter.Id.Value.ToString()
            && value.Label == parameter.Name);

        int historyCount = session.Document!.History.Count;
        owner.Value = secondMapping.Steps.Id.Value.ToString();
        source.Value = MappingSource.LogicalParameter.ToString();
        parameterReference.Value = parameter.Id.Value.ToString();
        session.ApplyObjectProperties(workspace, [owner, source, parameterReference]);

        Assert.Equal(MappingSource.LogicalParameter, step.Source);
        Assert.Equal(parameter.Id, step.LogicalParameterId);
        Assert.Empty(mapping.Steps);
        Assert.Same(step, Assert.Single(secondMapping.Steps));
        Assert.Equal(historyCount + 1, session.Document.History.Count);
        session.Document.Undo();
        Assert.Same(step, Assert.Single(mapping.Steps));
        Assert.Empty(secondMapping.Steps);
        Assert.Equal(MappingSource.Constant, step.Source);
        Assert.Null(step.LogicalParameterId);
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
        CreateLogicalTrack(session, "Track");
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
        CreateLogicalTrack(session, "Track");
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
        CreateLogicalTrack(session, "Track");
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
        CreateLogicalTrack(session, "Track");
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
        CreateLogicalTrack(session, "Edited");
        CreateLogicalTrack(session, "Unrelated");
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
        CreateLogicalTrack(session, "Track");
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
    public async Task OpeningSegmentCentersItsViewportOnArrangementEditCursor()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Segment open cursor",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(
            track.Id,
            projectStartTick: 5_000,
            lengthTicks: 20_000,
            contentOffsetTick: 1_000));
        Segment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        arrangement.EditCursorTick = 14_000;

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        const long expectedLocalTick = 10_000;
        Assert.Equal(expectedLocalTick, workspace.EditCursorTick);
        Assert.Equal(expectedLocalTick - (workspace.TickSpan / 2), workspace.StartTick);

        arrangement.EditCursorTick = 18_000;
        session.ActiveWorkspace = arrangement;
        workspace.StartTick = 0;
        Assert.Same(workspace, session.OpenSegment(segment.Id));
        const long reopenedLocalTick = 14_000;
        Assert.Equal(reopenedLocalTick, workspace.EditCursorTick);
        Assert.Equal(reopenedLocalTick - (workspace.TickSpan / 2), workspace.StartTick);
    }

    [Fact]
    public async Task OpeningSegmentDoesNotMoveViewportForArrangementCursorOutsideSegment()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Segment open cursor outside",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(
            track.Id,
            projectStartTick: 5_000,
            lengthTicks: 20_000,
            contentOffsetTick: 1_000));
        Segment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        arrangement.EditCursorTick = 25_000;

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        Assert.Null(workspace.EditCursorTick);
        Assert.Equal(0, workspace.StartTick);
    }

    [Fact]
    public async Task OpeningPureMidiSegmentCentersItsViewportOnArrangementEditCursor()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "MIDI Segment open cursor",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        _ = Assert.Single(session.Project!.MidiChannelRoots);
        PureMidiTrack track = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(
            track.Id,
            projectStartTick: 7_000,
            lengthTicks: 20_000,
            contentOffsetTick: 2_000));
        MidiSegment segment = Assert.Single(track.Segments);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        arrangement.EditCursorTick = 17_000;

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        const long expectedLocalTick = 12_000;
        Assert.Equal(expectedLocalTick, workspace.EditCursorTick);
        Assert.Equal(expectedLocalTick - (workspace.TickSpan / 2), workspace.StartTick);
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
        CreateLogicalTrack(session, "Track");
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
    public async Task InvalidPitchDiagnosticNavigationUsesSafeLaneAndDoesNotCrash()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Invalid pitch navigation",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 0, 120, 60, 100));
        LogicalNote note = Assert.Single(segment.Notes);
        note.Note = -4;
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        session.RefreshWorkspace(arrangement);
        Assert.Equal(0, Assert.Single(arrangement.Snapshot!.SegmentPreviews[segment.Id].Notes).Pitch);
        DiagnosticRow diagnostic = new(
            "Error",
            "Compile",
            "MIDORA1320",
            "Invalid Logical Note.",
            "Logical Note",
            true,
            new SourceReference(
                TrackId: track.Id,
                SegmentId: segment.Id,
                LogicalNoteId: note.Id,
                Tick: note.StartTick));

        session.NavigateToDiagnostic(diagnostic);

        TimelineWorkspaceViewModel workspace = Assert.IsType<TimelineWorkspaceViewModel>(session.ActiveWorkspace);
        TimelineRenderItem rendered = Assert.Single(workspace.Snapshot!.Items);
        Assert.Equal(127, rendered.Lane);
        Assert.True(rendered.State.HasFlag(TimelineItemState.Invalid));
        Assert.Equal(note.Id, workspace.Selection.Primary);
    }

    [Fact]
    public async Task SegmentVelocityProjectionUsesStartTickWidthAndPitchZOrder()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Velocity projection",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        CreateLogicalTrack(session, "Track");
        LogicalTrack track = Assert.Single(session.Project!.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 960));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalNote(segment.Id, 120, 720, 84, 96));
        LogicalNote note = Assert.Single(segment.Notes);

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);
        TimelineRenderItem velocity = Assert.Single(workspace.VelocitySnapshot!.Items);

        Assert.Equal(TimelineItemKind.Velocity, velocity.Kind);
        Assert.Equal(note.StartTick, velocity.StartTick);
        Assert.Equal(note.StartTick + 1, velocity.EndTick);
        Assert.Equal(note.Note, velocity.ZIndex);
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

        TimelineRenderSnapshot snapshot = Assert.IsType<TimelineRenderSnapshot>(arrangement.Snapshot);
        TimelineRenderSnapshot ruler = Assert.IsType<TimelineRenderSnapshot>(arrangement.RulerSnapshot);
        TimelineRenderItem marker = Assert.Single(ruler.Items);
        Assert.Equal(TimelineItemKind.Marker, marker.Kind);
        Assert.Equal("Verse", marker.Label);
        Assert.True(marker.State.HasFlag(TimelineItemState.HitTestDisabled));
        Assert.True(snapshot.Items.Count >= 3);
        Assert.All(snapshot.Items, item =>
            Assert.True(item.State.HasFlag(TimelineItemState.HitTestDisabled)));
        Assert.Contains(snapshot.Items, item => item.Label.Contains("132.5 BPM", StringComparison.Ordinal));
        Assert.Contains(snapshot.Items, item => item.Label == "3/4");
        Assert.Contains(snapshot.Items, item => item.Label == "Verse");
    }

    [Fact]
    public async Task ConductorPropertiesApplyOnlyOnExplicitTransaction()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Conductor Properties",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        TempoChange tempo = Assert.Single(session.Project!.Conductor.Tempos);
        ProjectTreeNode conductorNode = session.ProjectTree.Single(value =>
            value.Kind == ProjectTreeNodeKind.Conductor);
        TimelineWorkspaceViewModel conductor = Assert.IsType<TimelineWorkspaceViewModel>(
            session.OpenWorkspace(conductorNode));
        session.SelectWorkspaceObject(conductor, tempo.Id);

        ObjectPropertiesViewModel properties = session.CreateObjectProperties(conductor);
        Assert.Equal("Tempo", properties.Title);
        Assert.Equal(tempo.Id, conductor.SelectedConductorEvent?.Id);
        Assert.True(ObjectPropertiesProjection.CanEditInPropertiesDialog(
            conductor,
            properties));
        Assert.DoesNotContain(properties.Fields, value =>
            value.Key.Contains("id", StringComparison.OrdinalIgnoreCase));
        PropertyField bpm = properties.Fields.Single(value =>
            value.Key == "conductor.bpm");
        int historyCount = session.Document!.History.Count;
        bpm.Value = "127.5";

        Assert.Equal(120m, session.Project.Conductor.Tempos.Single(value =>
            value.Id == tempo.Id).BeatsPerMinute);
        session.ApplyObjectProperties(conductor, [bpm]);

        Assert.Equal(127.5m, session.Project.Conductor.Tempos.Single(value =>
            value.Id == tempo.Id).BeatsPerMinute);
        Assert.Equal(historyCount + 1, session.Document.History.Count);

        session.Document.Undo();

        Assert.Equal(120m, session.Project.Conductor.Tempos.Single(value =>
            value.Id == tempo.Id).BeatsPerMinute);
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
        ObjectPropertiesViewModel subVoiceProperties = session.CreateObjectProperties(workspace);
        Assert.True(ObjectPropertiesProjection.CanEditInPropertiesDialog(
            workspace,
            subVoiceProperties));
        Assert.DoesNotContain(subVoiceProperties.Fields, item =>
            item.Key.StartsWith("subvoice.initial.", StringComparison.Ordinal));
        PropertyField subVoiceName = subVoiceProperties.Fields.Single(item =>
            item.Key == "subvoice.name");
        subVoiceName.Value = "Attack Layer";
        Assert.Equal("Attack", voice.Name);
        session.ApplyObjectProperties(workspace, [subVoiceName]);
        Assert.Equal("Attack Layer", voice.Name);

        TimelineRenderItem note = Assert.Single(workspace.SubVoiceNoteSnapshot!.Items);
        Assert.Equal(TimelineItemKind.TemplateNote, note.Kind);
        Assert.Equal(63, note.Lane);
        Assert.Equal(80 / 127d, note.Value, 10);
        TimelineRenderItem midiEvent = Assert.Single(workspace.SubVoiceEventSnapshot!.Items);
        Assert.Equal(TimelineItemKind.LogicalParameterPoint, midiEvent.Kind);
        Assert.DoesNotContain(workspace.SubVoiceEventSnapshot.Items, item => item.Kind == TimelineItemKind.TemplateNote);
        InstrumentRenderLane lane = Assert.Single(workspace.RenderLanes);
        Assert.Equal(MidiValueTarget.ControlChange(1), lane.Target);
        Assert.Equal("CC 1 - Modulation Wheel (MSB)", lane.Label);
        SubVoiceEventMapping[] noteMappings = voice.EventMappings.Where(item =>
            item.Target.EventKind == TemplateEventKind.Note).ToArray();
        SubVoiceEventMapping controlChangeMapping = voice.EventMappings.Single(item =>
            item.Target == TemplateEventMidiTargets.ToMappingTarget(
                MidiValueTarget.ControlChange(1)));
        Assert.NotEmpty(noteMappings);
        Assert.All(noteMappings, mapping => Assert.False(workspace.MappingChains.Single(item =>
            item.Id == mapping.Steps.Id).CanDelete));
        Assert.True(workspace.MappingChains.Single(item =>
            item.Id == controlChangeMapping.Steps.Id).CanDelete);
        session.Execute(ProjectDomainEditCommands.DeleteMappingChain(
            instrument.Id,
            controlChangeMapping.Steps.Id,
            nonEmptyDeletionConfirmed: true));
        session.RefreshWorkspace(workspace);
        InstrumentRenderLane rawEventLane = Assert.Single(workspace.RenderLanes);
        Assert.Equal(MidiValueTarget.ControlChange(1), rawEventLane.Target);
        Assert.Null(rawEventLane.EventMappingChainId);
        Assert.Single(workspace.SubVoiceEventSnapshot!.Items);
        Assert.DoesNotContain(workspace.MappingChains, item =>
            item.Id == controlChangeMapping.Steps.Id);
        session.Undo();
        session.RefreshWorkspace(workspace);
        Assert.Equal(
            controlChangeMapping.Steps.Id,
            Assert.Single(workspace.RenderLanes).EventMappingChainId);
        workspace.Selection.Replace(controlChangeMapping.Steps.Id);
        session.RefreshWorkspace(workspace);
        ObjectPropertiesViewModel mappingProperties = session.CreateObjectProperties(workspace);
        Assert.Equal("Mapping Chain", mappingProperties.Title);
        PropertyField enabledField = mappingProperties.Fields.Single(item =>
            item.Key == "mappingChain.enabled");
        Assert.True(enabledField.IsBoolean);
        Assert.True(enabledField.IsEditable);
        Assert.True(ObjectPropertiesProjection.CanEditInPropertiesDialog(
            workspace,
            mappingProperties));
        enabledField.BooleanValue = false;
        session.ApplyObjectProperties(workspace, [enabledField]);
        Assert.False(controlChangeMapping.Steps.IsEnabled);
        session.Undo();
        Assert.True(controlChangeMapping.Steps.IsEnabled);
        int renderLaneSelectionNotifications = 0;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workspace.ActiveRenderLaneIndex))
            {
                renderLaneSelectionNotifications++;
            }
        };
        workspace.RenderLanes.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                workspace.ActiveRenderLaneIndex = -1;
            }
        };
        session.RefreshWorkspace(workspace);
        Assert.Equal(0, workspace.ActiveRenderLaneIndex);
        Assert.Equal(2, renderLaneSelectionNotifications);
        Assert.Equal(voice.Id, workspace.ActiveSubVoiceId);
        Assert.Equal(62, workspace.ActiveRootPitch);
        Assert.Contains("override", workspace.ActiveSubVoiceContext, StringComparison.Ordinal);
        Assert.Contains(workspace.InitialStateEntries, item =>
            item.Target == "CC 7 - Channel Volume (MSB)" && item.Value == "100");
        workspace.Selection.Replace(midiEvent.Id);
        Assert.True(ObjectPropertiesProjection.CanEditInPropertiesDialog(
            workspace,
            session.CreateObjectProperties(workspace)));
        session.Execute(ProjectDomainEditCommands.DeleteTemplateEvents(
            instrument.Id,
            voice.Id,
            [midiEvent.Id]));
        session.RefreshWorkspace(workspace);
        InstrumentRenderLane retainedEmptyLane = Assert.Single(workspace.RenderLanes);
        Assert.Equal(MidiValueTarget.ControlChange(1), retainedEmptyLane.Target);
        Assert.NotNull(retainedEmptyLane.EventMappingChainId);
        Assert.Empty(workspace.SubVoiceEventSnapshot!.Items);
        workspace.Selection.Clear();
        session.RefreshWorkspace(workspace);
        ObjectPropertiesViewModel emptyProperties = session.CreateObjectProperties(workspace);
        Assert.NotEmpty(emptyProperties.Fields);
        Assert.Equal(instrument.Name, emptyProperties.Title);
        Assert.Equal("Event Instrument", emptyProperties.Context);
    }

    [Fact]
    public async Task ActivatingSubVoiceEditorSelectsTheRequestedVoiceAndSwitchesTheInnerSection()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "SubVoice navigation",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Second"));
        SubVoice second = instrument.SubVoices.Single(value => value.Name == "Second");
        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.ActiveSectionIndex = 0;

        session.ActivateSubVoiceEditor(workspace, second.Id);

        Assert.Equal(second.Id, workspace.Selection.Primary);
        Assert.Equal(second.Id, workspace.ActiveSubVoiceId);
        Assert.Equal(1, workspace.ActiveSectionIndex);
    }

    [Fact]
    public async Task SelectingOrEditingTemplateNoteKeepsTheActiveMidiEventLane()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "SubVoice lane selection",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateSubVoice(instrument.Id, "Voice"));
        SubVoice voice = instrument.SubVoices.Single(value => value.Name == "Voice");
        session.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            tick: 48,
            lengthTicks: 96,
            note: 64,
            velocity: 80));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id,
            voice.Id,
            tick: 72,
            controller: 1,
            value: 96));
        session.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id,
            voice.Id,
            tick: 96,
            controller: 74,
            value: 64));

        InstrumentWorkspaceViewModel workspace = session.OpenInstrument(instrument.Id);
        workspace.Selection.Replace(voice.Id);
        session.RefreshWorkspace(workspace);
        int desiredLane = workspace.RenderLanes
            .Select((lane, index) => (lane, index))
            .Single(value => value.lane.Target == MidiValueTarget.ControlChange(74))
            .index;
        workspace.ActiveRenderLaneIndex = desiredLane;
        workspace.RenderLanes.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                workspace.ActiveRenderLaneIndex = -1;
            }
        };

        TemplateEvent note = voice.Events.Single(value => value.Kind == TemplateEventKind.Note);
        workspace.Selection.Replace(note.Id);
        session.Execute(ProjectDomainEditCommands.UpdateTemplateNote(
            instrument.Id,
            voice.Id,
            note.Id,
            tick: 49,
            lengthTicks: note.LengthTicks,
            note: note.Number,
            velocity: note.Value,
            followPitchDelta: note.FollowPitchDelta));

        Assert.Equal(
            MidiValueTarget.ControlChange(74),
            workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target);
    }

    [Fact]
    public async Task SegmentParameterChoicesIncludeBoundDefinitionsWithoutExistingLanes()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Empty Parameter Lane",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Pressure",
            LogicalParameterType.Integer,
            0,
            127,
            0,
            127,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        ParameterLaneOption option = Assert.Single(workspace.ParameterLaneOptions);
        Assert.Equal(parameter.Id, option.ParameterId);
        Assert.Null(option.LaneId);
        Assert.Equal("Pressure · Empty", option.Label);
        int parameterLaneSelectionNotifications = 0;
        workspace.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(workspace.ActiveParameterLaneIndex))
            {
                parameterLaneSelectionNotifications++;
            }
        };
        workspace.ParameterLaneOptions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                workspace.ActiveParameterLaneIndex = -1;
            }
        };
        session.RefreshWorkspace(workspace);
        Assert.Equal(0, workspace.ActiveParameterLaneIndex);
        Assert.Equal(2, parameterLaneSelectionNotifications);
    }

    [Fact]
    public async Task SegmentLogicalParameterLaneProjectsOnlyEditablePointSet()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Point-set Parameter Lane",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Pressure",
            LogicalParameterType.Integer,
            0,
            127,
            0,
            127,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));
        Segment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            segment.Id,
            parameter.Id));
        LogicalParameterLane lane = Assert.Single(segment.ParameterLanes);
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            0,
            32,
            CurveInterpolation.Step));
        session.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            240,
            96,
            CurveInterpolation.Step));

        TimelineWorkspaceViewModel workspace = session.OpenSegment(segment.Id);

        Assert.Equal(2, workspace.ParameterSnapshot!.Items.Count);
        Assert.All(workspace.ParameterSnapshot.Items, item =>
            Assert.Equal(TimelineItemKind.LogicalParameterPoint, item.Kind));
    }

    [Fact]
    public async Task EventInstrumentTimelineViewStateIsIndependentAndRetainedPerWorkspace()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Instrument viewport state",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("First"));
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Second"));
        EventInstrument first = session.Project!.EventInstruments[0];
        EventInstrument second = session.Project.EventInstruments[1];
        InstrumentWorkspaceViewModel firstWorkspace = session.OpenInstrument(first.Id);
        firstWorkspace.TimelineStartTick = 120;
        firstWorkspace.TimelineTickSpan = 960;
        firstWorkspace.TimelineFirstLane = 24;
        firstWorkspace.TimelineLaneHeight = 31;
        firstWorkspace.BottomEditorRowHeight = new System.Windows.GridLength(260);
        firstWorkspace.IsLowerEditorVisible = false;
        firstWorkspace.ActiveLowerEditorIndex = 1;
        firstWorkspace.EventValueScrollOffset = 0.35;

        InstrumentWorkspaceViewModel secondWorkspace = session.OpenInstrument(second.Id);
        secondWorkspace.TimelineStartTick = 480;
        secondWorkspace.TimelineTickSpan = 1920;
        secondWorkspace.TimelineFirstLane = 72;
        secondWorkspace.TimelineLaneHeight = 14;
        secondWorkspace.ActiveLowerEditorIndex = 0;
        _ = session.OpenArrangement();

        Assert.Same(firstWorkspace, session.OpenInstrument(first.Id));
        Assert.Equal(120, firstWorkspace.TimelineStartTick);
        Assert.Equal(960, firstWorkspace.TimelineTickSpan);
        Assert.Equal(24, firstWorkspace.TimelineFirstLane);
        Assert.Equal(31, firstWorkspace.TimelineLaneHeight);
        Assert.Equal(0, firstWorkspace.BottomEditorRowHeight.Value);
        firstWorkspace.IsLowerEditorVisible = true;
        Assert.Equal(260, firstWorkspace.BottomEditorRowHeight.Value);
        Assert.Equal(1, firstWorkspace.ActiveLowerEditorIndex);
        Assert.Equal(0.35, firstWorkspace.EventValueScrollOffset);
        Assert.Same(secondWorkspace, session.OpenInstrument(second.Id));
        Assert.Equal(480, secondWorkspace.TimelineStartTick);
        Assert.Equal(1920, secondWorkspace.TimelineTickSpan);
        Assert.Equal(72, secondWorkspace.TimelineFirstLane);
        Assert.Equal(14, secondWorkspace.TimelineLaneHeight);
        Assert.Equal(0, secondWorkspace.ActiveLowerEditorIndex);
    }

    [Fact]
    public async Task ArrangementProjectsBoundInstrumentColorAndConductorUsesLargerLaneScale()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Timeline color",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
        EventInstrument instrument = Assert.Single(session.Project!.EventInstruments);
        session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentColor(
            instrument.Id,
            new MidoraColor(0x33, 0x66, 0x99)));
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Track", instrument.Id));
        LogicalTrack track = Assert.Single(session.Project.Tracks);
        session.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 480));

        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        TimelineRenderSnapshot snapshot = Assert.IsType<TimelineRenderSnapshot>(arrangement.Snapshot);
        TimelineRenderItem segment = Assert.Single(snapshot.Items, value => value.Kind == TimelineItemKind.Segment);
        ProjectTreeNode conductorNode = session.ProjectTree.Single(value =>
            value.Kind == ProjectTreeNodeKind.Conductor);
        TimelineWorkspaceViewModel conductor = Assert.IsType<TimelineWorkspaceViewModel>(
            session.OpenWorkspace(conductorNode));

        Assert.Equal(0xff336699u, segment.AccentColor);
        Assert.Equal(0xff336699u, snapshot.LaneColors[segment.Lane]);
        Assert.Equal(27, conductor.LaneHeight);
    }

    [Fact]
    public async Task ArrangementTrackSelectionDefaultsClosedSurvivesRebuildAndClearsOnDelete()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Arrangement selection",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        PureMidiTrack track = Assert.Single(session.Project!.PureMidiTracks);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();

        Assert.False(arrangement.IsEventInstrumentPaneVisible);
        arrangement.SelectedArrangementTrackId = track.Id;
        arrangement.IsConductorTrackSelected = false;
        session.Execute(ProjectDomainEditCommands.RenamePureMidiTrack(track.Id, "Renamed"));

        Assert.Equal(track.Id, arrangement.SelectedArrangementTrackId);
        Assert.False(arrangement.IsConductorTrackSelected);

        session.Execute(ProjectDomainEditCommands.DeletePureMidiTrack(
            track.Id,
            nonEmptyDeletionConfirmed: false));

        Assert.Null(arrangement.SelectedArrangementTrackId);
    }

    [Fact]
    public async Task MidiImportActivatesOneDetachedProjectCandidate()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Previous",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        StandardMidiFileTrack source = new(
            240,
            [
                StandardMidiFileEvent.Text(
                    0,
                    StandardMidiFile.TrackNameMetaType,
                    "Imported Track"),
                StandardMidiFileEvent.ChannelVoice(
                    0,
                    MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(
                    120,
                    MidiMessage.NoteOff(0, 60, 23))
            ]);
        byte[] file = StandardMidiFile.EncodeType1(192, [source]);

        IReadOnlyList<MidiProjectImportDiagnostic> diagnostics =
            await session.ImportMidiBytesAsNewProjectAsync(file, "Imported Project");

        Assert.DoesNotContain(diagnostics, value => value.Severity == DiagnosticSeverity.Warning);
        Assert.Equal("Imported Project", session.Project!.Metadata.ProjectName);
        Assert.Equal(192, session.Project.TicksPerQuarterNote);
        MidiChannelRoot root = Assert.Single(session.Project.MidiChannelRoots);
        PureMidiTrack track = Assert.Single(session.Project.PureMidiTracks);
        Assert.Equal(root.Id, track.MidiChannelRootId);
        Assert.Equal("Imported Track", track.Name);
        Assert.Equal(23, Assert.Single(Assert.Single(track.Segments).Notes).NoteOffVelocity);
        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        Assert.Equal(
            [
                ArrangementLaneKind.Conductor,
                ArrangementLaneKind.PureMidiTrack
            ],
            arrangement.Snapshot!.ArrangementLanes.Select(value => value.Kind));
    }

    [Fact]
    public async Task ArrangementAndDetailsProjectPureMidiHierarchyAndOpaqueData()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Pure MIDI UI",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        MidiChannelRoot root = Assert.Single(session.Project!.MidiChannelRoots);
        PureMidiTrack track = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
        MidiSegment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            segment.Id,
            startTick: 0,
            lengthTicks: 120,
            key: 60,
            noteOnVelocity: 100));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            tick: 32,
            DirectMidiChannelEventKind.ControlChange,
            data1: 11,
            data2: 96));
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
            segment.Id,
            tick: 48,
            DirectMidiChannelEventKind.NoteOn,
            data1: 67,
            data2: 80));
        OpaqueMidiEvent opaque = new(session.Project)
        {
            Tick = 64,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x01,
            Payload = [0xde, 0xad, 0xbe, 0xef],
            Order = 99
        };
        segment.OpaqueEvents.Add(opaque);

        TimelineWorkspaceViewModel arrangement = session.OpenArrangement();
        session.RefreshWorkspace(arrangement);
        TimelineRenderSnapshot arrangementSnapshot = Assert.IsType<TimelineRenderSnapshot>(
            arrangement.Snapshot);
        Assert.Equal(
            [ArrangementLaneKind.Conductor, ArrangementLaneKind.PureMidiTrack],
            arrangementSnapshot.ArrangementLanes.Select(value => value.Kind));
        ArrangementLaneDescriptor trackLane = arrangementSnapshot.ArrangementLanes.Single(value =>
            value.Kind == ArrangementLaneKind.PureMidiTrack);
        Assert.Equal("Auto Melodic", arrangementSnapshot.LaneSecondaryLabels[trackLane.Lane]);
        TimelineSegmentPreview preview = Assert.Single(arrangementSnapshot.SegmentPreviews).Value;
        Assert.Single(preview.Notes);
        // Unpaired raw Note messages stay editable in the Event Lane, but the
        // Arrangement overlay is reserved for non-Note MIDI events.
        Assert.Equal(2, preview.Events.Count);

        TimelineWorkspaceViewModel editor = session.OpenSegment(segment.Id);
        editor.Selection.Replace(opaque.Id);
        session.RefreshWorkspace(editor);

        ObjectPropertiesViewModel opaqueProperties = session.CreateObjectProperties(editor);
        Assert.Equal("Imported MIDI Event", opaqueProperties.Title);
        Assert.Equal("DEADBEEF", opaqueProperties.Fields.Single(
            value => value.Key == "opaqueMidi.payload").Value);
        Assert.Equal("4", opaqueProperties.Fields.Single(
            value => value.Key == "opaqueMidi.payloadLength").Value);

        editor.Selection.Clear();
        session.RefreshWorkspace(editor);
        ObjectPropertiesViewModel clearedProperties = session.CreateObjectProperties(editor);
        Assert.NotEqual("Imported MIDI Event", clearedProperties.Title);
    }

    [Fact]
    public async Task EditingPureMidiSegmentKeepsItsEditorWorkspaceOpen()
    {
        await using DesktopSessionController session = new();
        await session.CreateProjectAsync(new NewProjectCreationRequest
        {
            ProjectName = "Pure MIDI editor lifetime",
            PersistenceMode = NewProjectPersistenceMode.CreateUnsaved
        });
        session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI Track"));
        _ = Assert.Single(session.Project!.MidiChannelRoots);
        PureMidiTrack track = Assert.Single(session.Project.PureMidiTracks);
        session.Execute(ProjectDomainEditCommands.CreateMidiSegment(track.Id, 0, 480));
        MidiSegment segment = Assert.Single(track.Segments);
        session.Execute(ProjectDomainEditCommands.CreateDirectMidiNote(
            segment.Id,
            startTick: 0,
            lengthTicks: 120,
            key: 60,
            noteOnVelocity: 100));
        DirectMidiNote note = Assert.Single(segment.Notes);
        TimelineWorkspaceViewModel editor = session.OpenSegment(segment.Id);

        session.Execute(ProjectDomainEditCommands.MoveDirectMidiNotes(
            segment.Id,
            [note.Id],
            tickDelta: 24,
            keyDelta: 1));

        Assert.Contains(editor, session.Workspaces);
        Assert.Same(editor, session.OpenSegment(segment.Id));
        Assert.True(editor.Snapshot!.TryGetItem(note.Id, out TimelineRenderItem rendered));
        Assert.Equal(24, rendered.StartTick);
        Assert.Equal(127 - 61, rendered.Lane);
    }

    [Fact]
    public void ArrangementKeepsDamagedTrackPlaceholdersVisibleInFormalOrder()
    {
        MidoraProject project = new(480);
        MidoraId damagedTrackId = project.AllocateStableId();
        project.DamagedLogicalTracks.Add(new(
            damagedTrackId,
            "Broken Track",
            "logical-tracks/broken.pb",
            "track payload is damaged",
            0));
        MidoraId damagedMidiTrackId = project.AllocateStableId();
        project.DamagedPureMidiTracks.Add(new(
            damagedMidiTrackId,
            "Broken MIDI Track",
            "pure-midi-tracks/broken.pb",
            "track payload is damaged",
            1));
        project.ArrangementTracks.Add(new(
            ArrangementTrackKind.LogicalTrack,
            damagedTrackId));
        project.ArrangementTracks.Add(new(
            ArrangementTrackKind.PureMidiTrack,
            damagedMidiTrackId));
        TimelineEditorSettings settings = new();
        settings.Reset(arrangement: true, project.TicksPerQuarterNote);
        TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            "Arrangement",
            TimelineWorkspaceMode.Arrangement,
            settings);

        workspace.Rebuild(project, revision: 1);

        Assert.Equal(
            [
                ArrangementLaneKind.Conductor,
                ArrangementLaneKind.DamagedLogicalTrack,
                ArrangementLaneKind.DamagedPureMidiTrack
            ],
            workspace.Snapshot!.ArrangementLanes.Select(value => value.Kind));
        Assert.Contains("[Damaged] Broken Track", workspace.Snapshot.LaneLabels);
        Assert.Contains("[Damaged] Broken MIDI Track", workspace.Snapshot.LaneLabels);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                if (condition()) return;
            }
            catch (InvalidOperationException)
            {
                // The production UI serializes these projections on Dispatcher;
                // headless tests may observe the short collection replacement window.
            }
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected desktop projection state was not reached.");
            }
            await Task.Delay(10);
        }
    }

    private static void CreateLogicalTrack(
        DesktopSessionController session,
        string name)
    {
        if (session.Project!.EventInstruments.Count == 0)
        {
            session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Test Instrument"));
        }
        session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
            name,
            session.Project.EventInstruments[0].Id));
    }

}
