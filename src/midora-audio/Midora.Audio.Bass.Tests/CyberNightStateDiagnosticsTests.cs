using Midora.Application;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Audio.Bass.Tests;

public sealed class CyberNightStateDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void OptInCh1RangeRestoreMatchesTheFromStartChannelState()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_CYBER_NIGHT_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_CYBER_NIGHT_MIDI_PATH to run the Cyber Night state gate.");
            return;
        }

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidoraProject project = imported.Project;
            MidiChannelRoot root = Assert.Single(project.MidiChannelRoots, value =>
                value.FixedZeroBasedPort == 0 && value.FixedZeroBasedChannel == 0);
            string[] trackNames = project.PureMidiTracksInArrangementOrder()
                .Where(track => track.MidiChannelRootId == root.Id)
                .Select(track => track.Name)
                .ToArray();
            Assert.Contains("NOTE 1", trackNames);
            Assert.Contains("CC 1-1", trackNames);
            Assert.Contains("CC 1-2", trackNames);

            using ProjectCompilationSession session = new(project);
            foreach (long tick in new long[] { 26_112, 78_000 })
            {
                Dictionary<long, CanonicalMidiEvent> expected = session.LastAttempt
                    .QueryEventPages(0, tick)
                    .SelectMany(page => page.Items)
                    .Where(value => value.ZeroBasedPort == 0
                        && value.ZeroBasedChannel == 0
                        && value.SemanticTargetKey != long.MinValue)
                    .GroupBy(value => value.SemanticTargetKey)
                    .ToDictionary(group => group.Key, group => group.Last());

                CanonicalCompiledResult compiled = session.CompileForPlayback(tick, null);
                Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
                CanonicalMidiEvent[] restores = compiled.QueryEventPages(
                        tick,
                        tick + 1,
                        includeStateAtStart: true)
                    .SelectMany(page => page.Items)
                    .Where(value => value.Tick == tick
                        && value.ZeroBasedPort == 0
                        && value.ZeroBasedChannel == 0
                        && value.Role == CanonicalEventRole.RangeRestore
                        && value.SemanticTargetKey != long.MinValue)
                    .ToArray();
                Dictionary<long, CanonicalMidiEvent> actual = restores
                    .GroupBy(value => value.SemanticTargetKey)
                    .ToDictionary(group => group.Key, group => group.Last());

                Assert.NotEmpty(actual);
                foreach ((long target, CanonicalMidiEvent expectedValue) in expected)
                {
                    Assert.True(actual.TryGetValue(target, out CanonicalMidiEvent actualValue));
                    Assert.Equal(expectedValue.Message, actualValue.Message);
                }
                CanonicalMidiEvent[] directRestores = restores
                    .Where(value => value.Source.DirectMidiObjectId != default)
                    .ToArray();
                Assert.Equal(
                    directRestores.Length,
                    directRestores.Select(value => value.SemanticTargetKey).Distinct().Count());
                output.WriteLine(
                    $"tick={tick}; expectedTargets={expected.Count}; restores={restores.Length}; directRestores={directRestores.Length}");
            }
        }
        finally
        {
            imported.Project.Dispose();
        }
    }
}
