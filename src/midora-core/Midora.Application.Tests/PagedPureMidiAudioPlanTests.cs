using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class PagedPureMidiAudioPlanTests
{
    [Fact]
    public void PagedRootCarriesCacheOwnershipPresetSummaryAndBoundedIpcMetadata()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-paged-audio-plan-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            MidoraProject project = new(192);
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0,
                FixedZeroBasedChannel = 0,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 384,
                ContentOffsetTick = 0
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));

            string packPath = Path.Combine(directory, "track.mpk");
            using PureMidiContentPackWriter writer = new(packPath);
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 0,
                DirectMidiChannelEventKind.ControlChange, 0, 2, 1));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 0,
                DirectMidiChannelEventKind.ProgramChange, 5, 0, 2));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 0, 192, 60, 100, 0, 3, 4));
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(project);
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);

            Assert.True(compiled.IsConsumable, string.Join(Environment.NewLine, compiled.Diagnostics));
            Assert.True(compiled.HasPagedEvents);
            Assert.Single(compiled.PureMidiAudioFragments);
            MidiUnitFragmentRenderPlan fragment = Assert.Single(plan.UnitFragments.ToArray());
            MidiSegmentRenderPlan cacheSegment = Assert.Single(plan.Segments.ToArray());
            int rootSourceIndex = plan.FindSourceIndex(root.Id.Value);
            int trackSourceIndex = plan.FindSourceIndex(track.Id.Value);
            Assert.Equal(rootSourceIndex, fragment.SourceIndex);
            Assert.Equal(rootSourceIndex, cacheSegment.SourceIndex);
            Assert.Contains(
                new MidiRenderCacheSourceBinding(trackSourceIndex, rootSourceIndex),
                plan.CacheSourceBindings.ToArray());
            Assert.Contains((2 * 128) + 5, plan.ReferencedPresetKeys.ToArray());

            string planPath = Path.Combine(directory, "plan.mdap");
            MidiRenderPlanFile.Write(
                planPath,
                plan.WithEventStreamDescriptor(new("Midora.Test.Events", packPath)));
            MidiRenderPlan restored = MidiRenderPlanFile.Read(planPath);
            Assert.Equal(plan.CacheSourceBindings.ToArray(), restored.CacheSourceBindings.ToArray());
            Assert.Equal(plan.ReferencedPresetKeys.ToArray(), restored.ReferencedPresetKeys.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
