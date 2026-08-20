using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

internal static class PureMidiContentPackPersistenceV1
{
    public static ManifestFileEntryJsonV1 Materialize(
        PureMidiTrack track,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        string packagePath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
        string filePath = Path.Combine(
            contentRoot,
            packagePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        PureMidiContentPack? reusable = FindReusablePack(track);
        if (reusable is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream destination = new(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.SequentialScan);
            reusable.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }
        else
        {
            WriteMergedPack(track, filePath, cancellationToken);
        }

        using FileStream hashInput = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.SequentialScan);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(hashInput));
        return new()
        {
            Path = packagePath,
            Kind = "pure-midi-content-pack",
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            Sha256 = sha256
        };
    }

    private static PureMidiContentPack? FindReusablePack(PureMidiTrack track)
    {
        PureMidiContentPack? candidate = null;
        HashSet<MidoraId> currentSegmentIds = track.Segments.Select(value => value.Id).ToHashSet();
        foreach (MidiSegment segment in track.Segments)
        {
            PureMidiContentPack? owner = segment.TryGetPristineContentPack();
            if (owner is null)
            {
                if (segment.Notes.Count != 0
                    || segment.ChannelEvents.Count != 0
                    || segment.OpaqueEvents.Count != 0)
                {
                    return null;
                }
                continue;
            }
            if (candidate is not null && !ReferenceEquals(candidate, owner)) return null;
            candidate = owner;
        }
        if (candidate is null) return null;
        return candidate.SegmentIds.All(currentSegmentIds.Contains) ? candidate : null;
    }

    private static void WriteMergedPack(
        PureMidiTrack track,
        string filePath,
        CancellationToken cancellationToken)
    {
        using PureMidiContentPackWriter writer = new(filePath);
        long recordIndex = 0;
        foreach (MidiSegment segment in track.Segments)
        {
            foreach (DirectMidiNote value in segment.Notes)
            {
                CheckCancellation();
                writer.AddNote(segment.Id, new(
                    value.Id,
                    value.StartTick,
                    value.LengthTicks,
                    value.Key,
                    value.NoteOnVelocity,
                    value.NoteOffVelocity,
                    value.NoteOnOrder,
                    value.NoteOffOrder));
            }
            foreach (DirectMidiChannelEvent value in segment.ChannelEvents)
            {
                CheckCancellation();
                writer.AddChannelEvent(segment.Id, new(
                    value.Id,
                    value.Tick,
                    value.Kind,
                    value.Data1,
                    value.Data2,
                    value.Order));
            }
            foreach (OpaqueMidiEvent value in segment.OpaqueEvents)
            {
                CheckCancellation();
                writer.AddOpaqueEvent(segment.Id, new(
                    value.Id,
                    value.Tick,
                    value.Kind,
                    value.MetaType,
                    value.Payload,
                    value.Order));
            }
        }
        using PureMidiContentPack completed = writer.Complete();

        void CheckCancellation()
        {
            if ((recordIndex++ & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
