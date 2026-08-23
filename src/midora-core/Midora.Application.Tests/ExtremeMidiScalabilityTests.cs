using System.Diagnostics;
using Midora.Audio;
using Midora.Compiler;
using Midora.Playback;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class ExtremeMidiScalabilityTests(ITestOutputHelper output)
{
    [Fact]
    public void OptInSamplePagedSelectionEditUsesBoundedTargetedWork()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_SCALE_MIDI_PATH to run the opt-in large-MIDI edit gate.");
            return;
        }

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            int requestedEditCount = int.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_EDIT_COUNT"),
                    out int configuredEditCount)
                ? Math.Max(1, configuredEditCount)
                : 4_096;
            DirectMidiNoteValue[] selected = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .Take(requestedEditCount)
                .ToArray();
            Assert.NotEmpty(selected);
            long originalFirstLength = selected[0].LengthTicks;
            IProjectEditCommand command = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                segment.Id,
                selected.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1);
            Stopwatch prepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit sourceEdit = command.Prepare(imported.Project);
            prepareTimer.Stop();
            Stopwatch collisionBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(
                imported.Project,
                sourceEdit);
            collisionBaselineTimer.Stop();

            Stopwatch timer = Stopwatch.StartNew();
            edit.Apply(imported.Project);
            timer.Stop();
            Assert.True(segment.Notes.TryGetById(selected[0].Id, out DirectMidiNote? edited));
            Assert.NotNull(edited);
            Assert.Equal(originalFirstLength + 1, edited!.LengthTicks);

            Stopwatch undoTimer = Stopwatch.StartNew();
            edit.Undo(imported.Project);
            undoTimer.Stop();
            Assert.Equal(originalFirstLength, edited.LengthTicks);

            Stopwatch movePrepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit moveSource = ProjectDomainEditCommands.MoveDirectMidiNotes(
                segment.Id,
                selected.Select(static value => value.Id).ToArray(),
                tickDelta: 1,
                keyDelta: 0).Prepare(imported.Project);
            movePrepareTimer.Stop();
            Stopwatch moveBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit move = ExactTimelineCollisionPolicy.Wrap(imported.Project, moveSource);
            moveBaselineTimer.Stop();
            Stopwatch moveTimer = Stopwatch.StartNew();
            move.Apply(imported.Project);
            moveTimer.Stop();
            Stopwatch moveUndoTimer = Stopwatch.StartNew();
            move.Undo(imported.Project);
            moveUndoTimer.Stop();
            Assert.True(segment.Notes.TryGetById(selected[0].Id, out _));
            long createTick = checked(segment.ContentEndTick + 1);
            Stopwatch createPrepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit createSource = ProjectDomainEditCommands.CreateDirectMidiNote(
                segment.Id,
                createTick,
                lengthTicks: 1,
                key: 0,
                noteOnVelocity: 100).Prepare(imported.Project);
            createPrepareTimer.Stop();
            Stopwatch createBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit create = ExactTimelineCollisionPolicy.Wrap(imported.Project, createSource);
            createBaselineTimer.Stop();
            Stopwatch createApplyTimer = Stopwatch.StartNew();
            create.Apply(imported.Project);
            createApplyTimer.Stop();
            output.WriteLine(
                $"notes={segment.Notes.Count}; selected={selected.Length}; "
                + $"prepare={prepareTimer.Elapsed}; collisionBaseline={collisionBaselineTimer.Elapsed}; "
                + $"edit={timer.Elapsed}; undo={undoTimer.Elapsed}; "
                + $"movePrepare={movePrepareTimer.Elapsed}; moveBaseline={moveBaselineTimer.Elapsed}; "
                + $"move={moveTimer.Elapsed}; moveUndo={moveUndoTimer.Elapsed}; "
                + $"createPrepare={createPrepareTimer.Elapsed}; createBaseline={createBaselineTimer.Elapsed}; "
                + $"createApply={createApplyTimer.Elapsed}");
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void OptInSampleImportAndCompileRemainPaged()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_SCALE_MIDI_PATH to run the opt-in large-MIDI gate.");
            return;
        }

        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), $"Scale sample does not exist: {path}");
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        Process process = Process.GetCurrentProcess();
        Stopwatch importTimer = Stopwatch.StartNew();
        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        importTimer.Stop();
        try
        {
            Stopwatch compileTimer = Stopwatch.StartNew();
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(imported.Project);
            compileTimer.Stop();

            Stopwatch planTimer = Stopwatch.StartNew();
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);
            planTimer.Stop();
            Stopwatch windowTimer = Stopwatch.StartNew();
            long startupEventCount = plan.EventPageProvider?.Query(
                    0,
                    Math.Min(plan.TotalFrameCount, 96_000))
                .LongCount() ?? plan.Ports.ToArray().Sum(port => (long)port.Events.Length);
            windowTimer.Stop();

            if (string.Equals(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_QUERY_MIDPOINT"),
                    "1",
                    StringComparison.Ordinal))
            {
                long midpointTick = compiled.StartTick
                    + ((compiled.EndTick - compiled.StartTick) / 2);
                long midpointEndTick = Math.Min(
                    compiled.EndTick,
                    checked(midpointTick + imported.Project.TicksPerQuarterNote * 2L));
                if (midpointEndTick > midpointTick)
                {
                    Stopwatch midpointTimer = Stopwatch.StartNew();
                    long midpointEventCount = compiled.QueryMidiRenderEventPages(
                            midpointTick,
                            midpointEndTick,
                            includeStateAtStart: true)
                        .Sum(page => (long)page.Items.Count);
                    midpointTimer.Stop();
                    output.WriteLine(
                        $"midpointTick={midpointTick}; midpointWindow={midpointTimer.Elapsed}; midpointEvents={midpointEventCount}");
                }
            }

            if (long.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_DIAGNOSTIC_FRAME"),
                    out long diagnosticFrame)
                && plan.EventPageProvider is not null)
            {
                long diagnosticStart = Math.Max(0, diagnosticFrame - 48_000);
                ScheduledPortMidiMessage[] diagnosticEvents = plan.EventPageProvider.Query(
                        diagnosticStart,
                        Math.Min(plan.TotalFrameCount, diagnosticFrame + 48_001))
                    .ToArray();
                output.WriteLine($"diagnosticFrame={diagnosticFrame}; events={diagnosticEvents.Length}");
                IGrouping<(long Frame, int Unit), ScheduledPortMidiMessage>[] groups = diagnosticEvents
                    .GroupBy(value => (
                        Frame: value.Scheduled.SampleFrame,
                        Unit: value.ZeroBasedPortNumber * 16 + value.Scheduled.Message.ChannelNumber))
                    .OrderByDescending(value => value.Count())
                    .ThenBy(value => Math.Abs(value.Key.Frame - diagnosticFrame))
                    .Take(20)
                    .ToArray();
                foreach (IGrouping<(long Frame, int Unit), ScheduledPortMidiMessage> group in groups)
                {
                    string kinds = string.Join(",", group
                        .GroupBy(value => value.Scheduled.Message.MessageType)
                        .OrderBy(value => value.Key)
                        .Select(value => $"{value.Key}:{value.Count()}"));
                    output.WriteLine(
                        $"frame={group.Key.Frame}; unit={group.Key.Unit}; count={group.Count()}; kinds={kinds}");
                }
            }

            Assert.True(compiled.IsConsumable);
            Assert.True(compiled.HasPagedEvents);
            Assert.Empty(compiled.Events.ToArray());
            Assert.True(compiled.TotalNoteOnEventCount > 0);
            Assert.InRange(
                imported.Project.PureMidiTracks
                    .SelectMany(track => track.Segments)
                    .Sum(segment => segment.Notes.Count),
                1,
                long.MaxValue);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            long retainedHeap = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
            MidiProjectImportMetrics metrics = Assert.IsType<MidiProjectImportMetrics>(imported.Metrics);
            output.WriteLine($"sample={path}");
            output.WriteLine($"fileBytes={metrics.SourceFileBytes}");
            output.WriteLine($"notes={metrics.ImportedNoteCount}; otherEvents={metrics.ImportedDirectEventCount}");
            output.WriteLine($"pages={metrics.ContentPageCount}; packBytes={metrics.ContentPackBytes}");
            output.WriteLine($"pass1={metrics.FirstPassElapsed}; pass2={metrics.SecondPassElapsed}; import={importTimer.Elapsed}; compile={compileTimer.Elapsed}");
            output.WriteLine($"plan={planTimer.Elapsed}; startupWindow={windowTimer.Elapsed}; startupEvents={startupEventCount}");
            output.WriteLine($"retainedManagedBytes={retainedHeap}; peakWorkingSetBytes={process.PeakWorkingSet64}");
        }
        finally
        {
            imported.Project.Dispose();
        }
    }
}
