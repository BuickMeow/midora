using System.Buffers.Binary;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PureMidiContentPackTests
{
    [Fact]
    public void PlaybackEndpointPagesAreGloballyOrderedAcrossLocalPageRuns()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "endpoints.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            const int noteCount = 20_000;
            using (PureMidiContentPackWriter writer = new(path))
            {
                for (int index = noteCount - 1; index >= 0; index--)
                {
                    writer.AddNote(segment.Id, new(
                        project.AllocateStableId(),
                        index,
                        100 + index % 7,
                        index % 128,
                        100,
                        0,
                        index * 2L,
                        index * 2L + 1));
                }
                writer.AddChannelEvent(segment.Id, new(
                    project.AllocateStableId(),
                    20,
                    DirectMidiChannelEventKind.ControlChange,
                    11,
                    80,
                    2));
                writer.AddChannelEvent(segment.Id, new(
                    project.AllocateStableId(),
                    10,
                    DirectMidiChannelEventKind.ProgramChange,
                    4,
                    0,
                    1));
                using PureMidiContentPack pack = writer.Complete();
                // One 20,000-record source Note page, two pages per Note endpoint
                // index, and one source/endpoint page for Channel events.
                Assert.Equal(7, pack.PageCount);
                IPureMidiPlaybackEndpointSource endpoints =
                    Assert.IsAssignableFrom<IPureMidiPlaybackEndpointSource>(
                        pack.GetSegmentSource(segment.Id));

                DirectMidiNoteValue[] active = endpoints
                    .QueryActiveNotes(15_050)
                    .ToArray();
                Assert.NotEmpty(active);
                Assert.All(
                    active,
                    value => Assert.True(
                        value.StartTick < 15_050
                        && value.StartTick + value.LengthTicks > 15_050));
                Assert.Equal(1, pack.PageCacheMissCount);

                DirectMidiNoteValue[] starts = endpoints
                    .QueryNoteStarts(15_000, 15_100)
                    .ToArray();
                Assert.Equal(100, starts.Length);
                Assert.Equal(
                    Enumerable.Range(15_000, 100).Select(value => (long)value),
                    starts.Select(value => value.StartTick));

                DirectMidiNoteValue[] ends = endpoints
                    .QueryNoteEnds(15_100, 15_220)
                    .ToArray();
                Assert.NotEmpty(ends);
                Assert.True(ends
                    .Select(value => value.StartTick + value.LengthTicks)
                    .SequenceEqual(ends
                        .Select(value => value.StartTick + value.LengthTicks)
                        .Order()));
                Assert.Equal(
                    [10L, 20L],
                    endpoints.QueryOrderedChannelEvents(0, 30)
                        .Select(value => value.Tick)
                        .ToArray());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousDevelopmentPackVersionIsRejected()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "old-version.mpk");
        try
        {
            using (PureMidiContentPackWriter writer = new(path))
            {
                using PureMidiContentPack completed = writer.Complete();
            }
            byte[] bytes = File.ReadAllBytes(path);
            bytes[6] = (byte)'2';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => PureMidiContentPack.Open(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RoundTrip_QueryAndCopyOnWriteRemainBoundedByPages()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 2_000,
                ContentOffsetTick = 0
            };
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Auto,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            MidoraId firstNoteId = project.AllocateStableId();
            MidoraId secondNoteId = project.AllocateStableId();
            MidoraId channelEventId = project.AllocateStableId();
            MidoraId opaqueEventId = project.AllocateStableId();
            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(segment.Id, new(
                    firstNoteId, 10, 100, 60, 100, 12, 1, 2));
                writer.AddNote(segment.Id, new(
                    secondNoteId, 1_000, 100, 72, 90, 0, 3, 4));
                writer.AddChannelEvent(segment.Id, new(
                    channelEventId, 20, DirectMidiChannelEventKind.ControlChange, 11, 127, 5));
                writer.AddOpaqueEvent(segment.Id, new(
                    opaqueEventId, 30, OpaqueMidiEventKind.Meta, 6, new byte[] { 1, 2, 3 }, 6));
                using PureMidiContentPack pack = writer.Complete();
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));

                Assert.Equal(2, segment.Notes.Count);
                Assert.Equal(firstNoteId, segment.Notes[0].Id);
                Assert.Equal(secondNoteId, Assert.Single(segment.Notes.Query(900, 1_200)).Id);
                Assert.Equal(channelEventId, Assert.Single(segment.ChannelEvents.Query(0, 100)).Id);
                Assert.Equal([1, 2, 3], Assert.Single(segment.OpaqueEvents.Query(0, 100)).Payload);

                DirectMidiNote changed = segment.Notes[0];
                changed.Key = 65;
                Assert.Equal(65, segment.Notes[0].Key);
                Assert.Equal(65, Assert.Single(segment.Notes.Query(0, 200)).Key);

                Assert.InRange(pack.DecodedCacheByteCount, 1, pack.DecodedCacheByteLimit);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptedDecodedPageIsRejectedWhenRead()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(), 10, 20, 60, 100, 0, 1, 2));
                using PureMidiContentPack completed = writer.Complete();
            }
            byte[] bytes = File.ReadAllBytes(path);
            bytes[48] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            using PureMidiContentPack pack = PureMidiContentPack.Open(path);
            IPureMidiSegmentContentSource source = pack.GetSegmentSource(segment.Id);
            Assert.Throws<InvalidDataException>(() => source.GetNote(0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SharedDecodedCacheAppliesOneProjectWideByteLimitAcrossPacks()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using PureMidiContentPackDecodedCache cache = new(
                PureMidiContentPackWriter.MaximumDecodedPageByteCount);
            MidoraProject project = new(192);
            MidiSegment firstSegment = new(project);
            MidiSegment secondSegment = new(project);
            byte[] payload = new byte[3 * 1024 * 1024];
            using PureMidiContentPack first = WritePack("first", firstSegment);
            using PureMidiContentPack second = WritePack("second", secondSegment);

            _ = first.GetSegmentSource(firstSegment.Id).GetOpaqueEvent(0);
            Assert.InRange(cache.ByteCount, payload.Length, cache.ByteLimit);
            _ = second.GetSegmentSource(secondSegment.Id).GetOpaqueEvent(0);

            Assert.InRange(cache.ByteCount, payload.Length, cache.ByteLimit);
            Assert.Equal(2, first.PageCacheMissCount + second.PageCacheMissCount);
            Assert.Equal(cache.ByteCount, first.DecodedCacheByteCount);
            Assert.Equal(cache.ByteCount, second.DecodedCacheByteCount);

            PureMidiContentPack WritePack(string name, MidiSegment segment)
            {
                string path = System.IO.Path.Combine(directory, name + ".mpk");
                using PureMidiContentPackWriter writer = new(path);
                writer.AddOpaqueEvent(segment.Id, new(
                    project.AllocateStableId(),
                    0,
                    OpaqueMidiEventKind.Meta,
                    1,
                    payload,
                    0));
                return writer.Complete(cache);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
