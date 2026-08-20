using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

internal sealed class PureMidiContentPackExtractionV1 : IDisposable
{
    private readonly MidoraProject _project;
    private readonly string _root;
    private readonly PureMidiContentPackDecodedCache _decodedCache;
    private bool _disposed;

    public PureMidiContentPackExtractionV1(MidoraProject project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Midora",
            "SessionContent",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _project.RegisterRuntimeResource(this);
        _decodedCache = new();
        _project.RegisterRuntimeResource(_decodedCache);
    }

    public async Task AttachAsync(
        PureMidiTrack track,
        string packagePath,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(track);
        string expectedPath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
        if (!string.Equals(packagePath, expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException("Pure MIDI Track content-pack path does not match its stable ID.");
        if (!manifestIndex.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifest)
            || manifest.Kind != "pure-midi-content-pack"
            || manifest.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException(
                "Pure MIDI content pack is absent from the manifest or has an invalid kind/schemaVersion.");
        }
        if (!entries.TryGetValue(packagePath, out ZipArchiveEntry? entry))
            throw new InvalidDataException("Pure MIDI content pack is absent from the Zip container.");

        string destinationPath = Path.Combine(_root, $"mt_{track.Id.Value}.mpk");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = entry.Open();
            await using FileStream destination = new(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, read));
                total = checked(total + read);
            }
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != entry.Length)
                throw new InvalidDataException("Pure MIDI content pack extracted length is inconsistent.");
            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, manifest.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("Pure MIDI content-pack SHA-256 does not match manifest.json.");
        }
        catch
        {
            TryDelete(destinationPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        PureMidiContentPack? pack = null;
        try
        {
            pack = PureMidiContentPack.Open(destinationPath, _decodedCache);
            HashSet<MidoraId> segmentIds = track.Segments.Select(value => value.Id).ToHashSet();
            if (pack.SegmentIds.Any(value => !segmentIds.Contains(value)))
                throw new InvalidDataException("Pure MIDI content pack references a Segment absent from its Track.");
            foreach (MidiSegment segment in track.Segments)
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            _project.RegisterRuntimeResource(pack);
            pack = null;
        }
        finally
        {
            pack?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Session cleanup is best effort; content is no longer reachable.
        }
        catch (UnauthorizedAccessException)
        {
            // Session cleanup is best effort; content is no longer reachable.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
