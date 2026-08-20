using Midora.AudioDevice;

namespace Midora.Audio.Bass;

internal sealed class AudioSegmentCacheStaging : IDisposable
{
    private readonly Entry[] _entries;
    private ReusableAudioReadLease? _readLease;
    private AudioCacheSessionStore.AudioRecoverySpool? _spool;
    private string? _journalDirectory;

    private AudioSegmentCacheStaging(
        MidiRenderPlan plan,
        AudioCacheSessionStore.AudioRecoverySpool spool,
        Entry[] entries,
        ReusableAudioReadLease? readLease,
        string? readManifestPath,
        string? journalDirectory)
    {
        Plan = plan;
        _spool = spool;
        FilePath = spool.Path;
        _entries = entries;
        _readLease = readLease;
        ReadManifestPath = readManifestPath;
        _journalDirectory = journalDirectory;
    }

    public MidiRenderPlan Plan { get; }
    public string FilePath { get; }
    public string? ReadManifestPath { get; }

    public static AudioSegmentCacheStaging? Create(
        MidiRenderPlan plan,
        IAudioPcmCacheSessionAccess? cache,
        string soundFontSha256,
        string nativeDirectory,
        int maximumSampleVoicesPerUnitStream,
        string manifestDirectory)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (cache is null || plan.Segments.IsEmpty
            || cache.AudioCacheSnapshot?.RetentionState
                == AudioCacheRetentionState.DisabledByPreference)
        {
            return null;
        }

        AudioUnitCacheStaging.ValidateSoundFontSha256(soundFontSha256);
        string nativeIdentity = AudioUnitCacheStaging.ComputeNativeIdentity(nativeDirectory);
        AudioFormat format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        bool retentionEnabled = cache.AudioCacheSnapshot?.RetentionState
            == AudioCacheRetentionState.Enabled;
        bool usePackJournal = retentionEnabled
            && cache.SupportsReusableAudioPackJournals;
        string[] keys = new string[plan.Segments.Length];
        AudioCacheGenerationBinding[] generations =
            new AudioCacheGenerationBinding[plan.Segments.Length];
        int keyIndex = 0;
        foreach (MidiSegmentRenderPlan segment in plan.Segments)
        {
            string key = MidiSegmentPcmCacheKey.Create(
                segment,
                plan.SampleRate,
                soundFontSha256,
                nativeIdentity,
                maximumSampleVoicesPerUnitStream);
            keys[keyIndex++] = key;
            generations[keyIndex - 1] = new(
                $"segment:{segment.TrackId}:{segment.SegmentId}",
                key);
        }
        cache.RegisterReusableAudioGenerations(generations);
        ReusableAudioReadLease? readLease = cache.AcquireReusableAudioReadLease(keys);
        long stagingByteLength = 0;
        for (int index = 0; index < plan.Segments.Length; index++)
        {
            MidiSegmentRenderPlan segment = plan.Segments[index];
            if (!usePackJournal
                && (readLease?.Entries.TryGetValue(
                    keys[index],
                    out ReusableAudioReadEntry? directEntry) != true
                    || directEntry!.PayloadLength != segment.PcmPayloadByteCount))
            {
                stagingByteLength = checked(
                    stagingByteLength + segment.PcmPayloadByteCount);
            }
        }
        List<ReusableAudioReadEntry> directEntries = [];
        AudioCacheSessionStore.AudioRecoverySpool spool =
            cache.CreateSparseTransientAudioSpool(stagingByteLength);
        List<Entry> entries = [];
        MidiSegmentRenderPlan[] segments = new MidiSegmentRenderPlan[plan.Segments.Length];
        try
        {
            FileStream staging = (FileStream)spool.Stream;
            byte[] headerBuffer = new byte[AudioPcmCachePayload.HeaderByteCount];
            long nextPayloadOffset = 0;
            for (int index = 0; index < segments.Length; index++)
            {
                MidiSegmentRenderPlan segment = plan.Segments[index];
                string key = keys[index];
                long payloadOffset = nextPayloadOffset;
                long expectedLength = segment.PcmPayloadByteCount;
                ReusableAudioReadEntry? directEntry = null;
                bool directHit = readLease is not null
                    && readLease.Entries.TryGetValue(key, out directEntry)
                    && directEntry.PayloadLength == expectedLength;
                bool hit = directHit;
                long copiedLength = 0;
                if (!directHit && !retentionEnabled)
                {
                    hit = cache.TryCopyReusableAudio(key, staging, out copiedLength);
                }
                if (!directHit && hit && !ValidateCopiedPayload(
                    staging,
                    payloadOffset,
                    copiedLength,
                    expectedLength,
                    format,
                    segment.FrameCount))
                {
                    cache.InvalidateReusableAudio(key);
                    hit = false;
                }
                if (directHit)
                {
                    directEntries.Add(directEntry!);
                }
                if (directHit)
                {
                    // Direct Pack hits have no payload in the transient spool.
                    // The unique negative binding is used only as Segment
                    // identity by the read-ahead schedule.
                    long directBinding = -2;
                    segments[index] = Clone(segment, key, directBinding, true);
                    entries.Add(new(key, directBinding, expectedLength, segment.EndFrame, true));
                    continue;
                }
                nextPayloadOffset = checked(nextPayloadOffset + expectedLength);
                staging.Position = payloadOffset;
                if (!hit && !retentionEnabled)
                {
                    staging.Position = payloadOffset;
                    staging.SetLength(payloadOffset);
                    segments[index] = Clone(segment, null, -1, false);
                    continue;
                }
                if (!hit && !usePackJournal)
                {
                    staging.Position = payloadOffset;
                    staging.SetLength(payloadOffset);
                    Span<byte> header = headerBuffer;
                    header.Clear();
                    AudioPcmCachePayload.WriteHeader(header, format, segment.FrameCount);
                    staging.Write(header);
                    staging.SetLength(checked(payloadOffset + expectedLength));
                }
                staging.Position = checked(payloadOffset + expectedLength);
                segments[index] = Clone(segment, key, payloadOffset, hit);
                entries.Add(new(key, payloadOffset, expectedLength, segment.EndFrame, hit));
            }
            staging.Flush(flushToDisk: true);
        }
        catch
        {
            spool.Dispose();
            readLease?.Dispose();
            throw;
        }

        if (entries.Count == 0)
        {
            spool.Dispose();
            readLease?.Dispose();
            return null;
        }
        MidiRenderPlan stagedPlan = new(
            plan.SampleRate,
            plan.TotalFrameCount,
            plan.Ports,
            plan.SourceIds,
            plan.InitiallyDisabledSourceIndices,
            plan.UnitFragments,
            segments,
            plan.UnitDescriptors,
            plan.EventPageProvider,
            plan.EventStreamDescriptor,
            plan.CacheSourceBindings,
            plan.ReferencedPresetKeys);
        string? readManifestPath = null;
        if (directEntries.Count != 0)
        {
            readManifestPath = Path.Combine(
                Path.GetFullPath(manifestDirectory),
                "segment-cache-read.marm");
            ReusableAudioReadManifest.Write(readManifestPath, directEntries);
        }
        string? journalDirectory = null;
        if (usePackJournal && entries.Any(static entry => !entry.Hit))
        {
            journalDirectory = AudioCachePackJournal.GetDirectoryPath(spool.Path);
            Directory.CreateDirectory(journalDirectory);
        }
        return new(
            stagedPlan,
            spool,
            entries.ToArray(),
            readLease,
            readManifestPath,
            journalDirectory);
    }

    public void PublishCompleted(
        IAudioPcmCacheSessionAccess cache,
        long completedRenderFrame)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (File.Exists(FilePath + ".invalidated")
            || _entries.All(entry => entry.Hit || entry.EndFrame > completedRenderFrame))
        {
            return;
        }
        AudioCachePublishSlice[] slices = _entries
            .Where(entry => !entry.Hit && entry.EndFrame <= completedRenderFrame)
            .Select(entry => new AudioCachePublishSlice(
                entry.Key,
                entry.PayloadOffset,
                entry.PayloadLength))
            .ToArray();
        if (slices.Length == 0)
        {
            return;
        }
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        if (spool is null)
        {
            return;
        }
        string? journalDirectory = Interlocked.Exchange(ref _journalDirectory, null);
        if (journalDirectory is not null)
        {
            cache.AdoptReusableAudioPackJournals(
                spool,
                journalDirectory,
                slices.Select(static slice => slice.Key).ToArray());
            return;
        }
        cache.QueueReusableAudioBatch(spool, slices);
    }

    public void Dispose()
    {
        ReusableAudioReadLease? lease = Interlocked.Exchange(ref _readLease, null);
        lease?.Dispose();
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        spool?.Dispose();
        string? journalDirectory = Interlocked.Exchange(ref _journalDirectory, null);
        TryDeleteDirectory(journalDirectory);
        TryDelete(FilePath + ".invalidated");
    }

    private static MidiSegmentRenderPlan Clone(
        MidiSegmentRenderPlan value,
        string? cacheKey,
        long cacheOffset,
        bool cacheHit) => new(
            value.TrackId,
            value.SegmentId,
            value.SourceIndex,
            value.StartFrame,
            value.EndFrame,
            value.SemanticFingerprint,
            cacheKey,
            cacheOffset,
            cacheHit);

    private static bool ValidateCopiedPayload(
        FileStream staging,
        long payloadOffset,
        long copiedLength,
        long expectedLength,
        AudioFormat format,
        long expectedFrameCount)
    {
        if (copiedLength != expectedLength)
        {
            return false;
        }
        long end = staging.Position;
        try
        {
            staging.Position = payloadOffset;
            Span<byte> header = stackalloc byte[AudioPcmCachePayload.HeaderByteCount];
            staging.ReadExactly(header);
            return AudioPcmCachePayload.ValidateHeader(header, format) == expectedFrameCount;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            staging.Position = end;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct Entry(
        string Key,
        long PayloadOffset,
        long PayloadLength,
        long EndFrame,
        bool Hit);
}
