using System.Diagnostics;
using Midora.Audio;
using Midora.Compiler;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class ExtremeMidiScalabilityTests(ITestOutputHelper output)
{
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
