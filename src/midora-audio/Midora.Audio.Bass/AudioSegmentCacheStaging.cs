using Midora.AudioDevice;

namespace Midora.Audio.Bass;

internal sealed class AudioSegmentCacheStaging : IDisposable
{
    private readonly Entry[] _entries;
    private AudioCacheSessionStore.AudioRecoverySpool? _spool;

    private AudioSegmentCacheStaging(
        MidiRenderPlan plan,
        AudioCacheSessionStore.AudioRecoverySpool spool,
        Entry[] entries)
    {
        Plan = plan;
        _spool = spool;
        FilePath = spool.Path;
        _entries = entries;
    }

    public MidiRenderPlan Plan { get; }
    public string FilePath { get; }

    public static AudioSegmentCacheStaging? Create(
        MidiRenderPlan plan,
        IAudioPcmCacheSessionAccess? cache,
        string soundFontPath,
        string nativeDirectory,
        int maximumSampleVoicesPerUnitStream)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (cache is null || plan.Segments.IsEmpty
            || cache.AudioCacheSnapshot?.RetentionState
                == AudioCacheRetentionState.DisabledByPreference)
        {
            return null;
        }

        string soundFontSha256 = AudioUnitCacheStaging.HashFile(soundFontPath);
        string nativeIdentity = AudioUnitCacheStaging.ComputeNativeIdentity(nativeDirectory);
        AudioFormat format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        bool retentionEnabled = cache.AudioCacheSnapshot?.RetentionState
            == AudioCacheRetentionState.Enabled;
        long maximumStagingBytes = 0;
        foreach (MidiSegmentRenderPlan segment in plan.Segments)
        {
            maximumStagingBytes = checked(maximumStagingBytes + segment.PcmPayloadByteCount);
        }
        AudioCacheSessionStore.AudioRecoverySpool spool =
            cache.CreateSparseTransientAudioSpool(maximumStagingBytes);
        List<Entry> entries = [];
        MidiSegmentRenderPlan[] segments = new MidiSegmentRenderPlan[plan.Segments.Length];
        try
        {
            FileStream staging = (FileStream)spool.Stream;
            byte[] headerBuffer = new byte[AudioPcmCachePayload.HeaderByteCount];
            for (int index = 0; index < segments.Length; index++)
            {
                MidiSegmentRenderPlan segment = plan.Segments[index];
                string key = MidiSegmentPcmCacheKey.Create(
                    segment,
                    plan.SampleRate,
                    soundFontSha256,
                    nativeIdentity,
                    maximumSampleVoicesPerUnitStream);
                cache.RegisterReusableAudioGeneration(
                    $"segment:{segment.TrackId}:{segment.SegmentId}",
                    key);
                long payloadOffset = staging.Position;
                long expectedLength = segment.PcmPayloadByteCount;
                bool hit = cache.TryCopyReusableAudio(key, staging, out long copiedLength);
                if (hit && !ValidateCopiedPayload(
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
                if (!hit && !retentionEnabled)
                {
                    staging.Position = payloadOffset;
                    staging.SetLength(payloadOffset);
                    segments[index] = Clone(segment, null, -1, false);
                    continue;
                }
                if (!hit)
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
            throw;
        }

        if (entries.Count == 0)
        {
            spool.Dispose();
            return null;
        }
        MidiRenderPlan stagedPlan = new(
            plan.SampleRate,
            plan.TotalFrameCount,
            plan.Ports,
            plan.SourceIds,
            plan.InitiallyDisabledSourceIndices,
            plan.UnitFragments,
            segments);
        return new(stagedPlan, spool, entries.ToArray());
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
        cache.QueueReusableAudioBatch(spool, slices);
    }

    public void Dispose()
    {
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        spool?.Dispose();
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

    private readonly record struct Entry(
        string Key,
        long PayloadOffset,
        long PayloadLength,
        long EndFrame,
        bool Hit);
}
