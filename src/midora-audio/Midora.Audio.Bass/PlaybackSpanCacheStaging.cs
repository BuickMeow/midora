using Midora.AudioDevice;

namespace Midora.Audio.Bass;

internal sealed class PlaybackSpanCacheStaging : IDisposable
{
    private AudioCacheSessionStore.AudioRecoverySpool? _spool;
    private int _captureInvalidated;

    private PlaybackSpanCacheStaging(
        AudioCacheSessionStore.AudioRecoverySpool spool,
        string key,
        bool hit,
        long payloadLength)
    {
        _spool = spool;
        FilePath = spool.Path;
        Key = key;
        Hit = hit;
        PayloadLength = payloadLength;
    }

    public string FilePath { get; }
    public string Key { get; }
    public bool Hit { get; }
    public long PayloadLength { get; }

    public static PlaybackSpanCacheStaging? Create(
        MidiRenderPlan plan,
        IAudioPcmCacheSessionAccess? cache,
        string soundFontSetCacheIdentity,
        string nativeDirectory,
        int maximumSampleVoicesPerUnitStream,
        AudioMasterSettings masterSettings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterSettings);
        AudioCacheSessionSnapshot? snapshot = cache?.AudioCacheSnapshot;
        if (cache is null
            || plan.EventPageProvider is not null
            || plan.EventStreamDescriptor is not null
            || snapshot?.RetentionState == AudioCacheRetentionState.DisabledByPreference)
        {
            return null;
        }

        // A full-span hit currently uses a contiguous async staging stream. Do
        // not materialize a range larger than the rolling high watermark before
        // the Worker starts; Segment Pack hits below it remain demand-driven.
        long maximumDirectStagingFrames = RollingAudioPreparationPolicy.MillisecondsToFrames(
            plan.SampleRate,
            RollingAudioPreparationPolicy.TargetHighWatermarkMilliseconds);
        if (plan.TotalFrameCount > maximumDirectStagingFrames)
        {
            return null;
        }

        AudioUnitCacheStaging.ValidateSoundFontSetCacheIdentity(soundFontSetCacheIdentity);
        string nativeIdentity = AudioUnitCacheStaging.ComputeNativeIdentity(nativeDirectory);
        string key = PlaybackSpanCacheKey.Create(
            plan,
            soundFontSetCacheIdentity,
            nativeIdentity,
            maximumSampleVoicesPerUnitStream,
            new PlaybackSpanMasterSettings(
                masterSettings.VolumeDecibels,
                masterSettings.LimiterCeiling,
                masterSettings.LimiterReleaseMilliseconds,
                masterSettings.LimiterEnabled));
        cache.RegisterReusableAudioGeneration(
            "playback-span:"
                + plan.SampleRate
                + ":"
                + plan.TotalFrameCount
                + ":"
                + string.Join(',', plan.SourceIds.ToArray()),
            key);
        AudioFormat format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        long payloadLength = checked(
            AudioPcmCachePayload.HeaderByteCount
            + (plan.TotalFrameCount * format.BytesPerFrame));
        AudioCacheSessionStore.AudioRecoverySpool spool =
            cache.CreateTransientAudioSpool(payloadLength);
        try
        {
            FileStream staging = (FileStream)spool.Stream;
            staging.Position = 0;
            bool hit = cache.TryCopyReusableAudio(key, staging, out long copiedLength);
            if (hit && !ValidatePayload(
                staging,
                copiedLength,
                payloadLength,
                format,
                plan.TotalFrameCount))
            {
                cache.InvalidateReusableAudio(key);
                hit = false;
            }
            if (!hit && snapshot?.RetentionState != AudioCacheRetentionState.Enabled)
            {
                spool.Dispose();
                return null;
            }
            if (!hit)
            {
                staging.Position = 0;
                staging.SetLength(payloadLength);
                Span<byte> header = stackalloc byte[AudioPcmCachePayload.HeaderByteCount];
                header.Clear();
                AudioPcmCachePayload.WriteHeader(header, format, plan.TotalFrameCount);
                staging.Write(header);
            }
            staging.Flush(flushToDisk: true);
            return new(spool, key, hit, payloadLength);
        }
        catch
        {
            spool.Dispose();
            throw;
        }
    }

    public void InvalidateCapture() => Interlocked.Exchange(ref _captureInvalidated, 1);

    public void PublishCompleted(
        IAudioPcmCacheSessionAccess cache,
        long completedRenderFrame,
        long totalFrameCount)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (Hit
            || Volatile.Read(ref _captureInvalidated) != 0
            || File.Exists(FilePath + ".invalidated")
            || completedRenderFrame != totalFrameCount
            || new FileInfo(FilePath).Length < PayloadLength)
        {
            return;
        }
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        if (spool is null)
        {
            return;
        }
        cache.QueueReusableAudioBatch(
            spool,
            [new AudioCachePublishSlice(Key, 0, PayloadLength)]);
    }

    public void Dispose()
    {
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        spool?.Dispose();
        TryDelete(FilePath + ".invalidated");
    }

    private static bool ValidatePayload(
        FileStream staging,
        long copiedLength,
        long expectedLength,
        AudioFormat format,
        long expectedFrameCount)
    {
        if (copiedLength != expectedLength)
        {
            return false;
        }
        try
        {
            staging.Position = 0;
            Span<byte> header = stackalloc byte[AudioPcmCachePayload.HeaderByteCount];
            staging.ReadExactly(header);
            return AudioPcmCachePayload.ValidateHeader(header, format) == expectedFrameCount;
        }
        catch (InvalidDataException)
        {
            return false;
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
}
