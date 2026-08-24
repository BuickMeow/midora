using Midora.AudioDevice;
using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio.Bass;

internal sealed class AudioUnitCacheStaging : IDisposable
{
    private static readonly string[] NativeFileNames =
        ["bass.dll", "bassmidi.dll", "basswasapi.dll"];
    private readonly Entry[] _entries;
    private AudioCacheSessionStore.AudioRecoverySpool? _spool;

    private AudioUnitCacheStaging(
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

    public static AudioUnitCacheStaging? Create(
        MidiRenderPlan plan,
        IAudioPcmCacheSessionAccess? cache,
        string soundFontSetCacheIdentity,
        string nativeDirectory,
        int maximumSampleVoicesPerUnitStream)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (cache is null || plan.UnitFragments.IsEmpty
            || cache.AudioCacheSnapshot?.RetentionState
                == AudioCacheRetentionState.DisabledByPreference)
        {
            return null;
        }

        ValidateSoundFontSetCacheIdentity(soundFontSetCacheIdentity);
        string nativeIdentity = ComputeNativeIdentity(nativeDirectory);
        AudioFormat format = new(plan.SampleRate, 2, AudioSampleFormat.Float32);
        bool retentionEnabled = cache.AudioCacheSnapshot?.RetentionState
            == AudioCacheRetentionState.Enabled;
        List<Entry> entries = [];
        MidiUnitFragmentRenderPlan[] fragments = new MidiUnitFragmentRenderPlan[
            plan.UnitFragments.Length];
        long maximumStagingBytes = 0;
        foreach (MidiUnitFragmentRenderPlan fragment in plan.UnitFragments)
        {
            maximumStagingBytes = checked(maximumStagingBytes + fragment.PcmPayloadByteCount);
        }
        AudioCacheSessionStore.AudioRecoverySpool spool =
            cache.CreateTransientAudioSpool(maximumStagingBytes);

        try
        {
            byte[] headerBuffer = new byte[AudioPcmCachePayload.HeaderByteCount];
            FileStream staging = (FileStream)spool.Stream;
            staging.Position = 0;
            for (int index = 0; index < fragments.Length; index++)
            {
                MidiUnitFragmentRenderPlan fragment = plan.UnitFragments[index];
                string key = MidiUnitPcmCacheKey.Create(
                    fragment,
                    plan.SampleRate,
                    soundFontSetCacheIdentity,
                    nativeIdentity,
                    maximumSampleVoicesPerUnitStream);
                long payloadOffset = staging.Position;
                long expectedLength = fragment.PcmPayloadByteCount;
                bool hit = cache.TryCopyReusableAudio(key, staging, out long copiedLength);
                if (hit && !ValidateCopiedPayload(
                    staging,
                    payloadOffset,
                    copiedLength,
                    expectedLength,
                    format,
                    fragment.EndFrame - fragment.StartFrame))
                {
                    cache.InvalidateReusableAudio(key);
                    hit = false;
                }

                if (!hit && !retentionEnabled)
                {
                    staging.Position = payloadOffset;
                    staging.SetLength(payloadOffset);
                    fragments[index] = Clone(fragment, null, -1, pcmCacheHit: false);
                    continue;
                }

                if (!hit)
                {
                    staging.Position = payloadOffset;
                    staging.SetLength(payloadOffset);
                    Span<byte> header = headerBuffer;
                    header.Clear();
                    AudioPcmCachePayload.WriteHeader(
                        header,
                        format,
                        fragment.EndFrame - fragment.StartFrame);
                    staging.Write(header);
                    staging.SetLength(checked(payloadOffset + expectedLength));
                }
                staging.Position = checked(payloadOffset + expectedLength);
                fragments[index] = Clone(fragment, key, payloadOffset, hit);
                entries.Add(new(
                    key,
                    payloadOffset,
                    expectedLength,
                    fragment.EndFrame,
                    hit));
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
            fragments,
            plan.Segments,
            plan.UnitDescriptors,
            plan.EventPageProvider,
            plan.EventStreamDescriptor,
            plan.CacheSourceBindings,
            plan.ReferencedPresetKeys);
        return new(stagedPlan, spool, entries.ToArray());
    }

    public void Dispose()
    {
        AudioCacheSessionStore.AudioRecoverySpool? spool =
            Interlocked.Exchange(ref _spool, null);
        spool?.Dispose();
        TryDelete(FilePath + ".invalidated");
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

        using FileStream source = new(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        foreach (Entry entry in _entries)
        {
            if (entry.Hit || entry.EndFrame > completedRenderFrame)
            {
                continue;
            }
            source.Position = entry.PayloadOffset;
            _ = cache.PublishReusableAudio(entry.Key, source, entry.PayloadLength);
        }
    }

    private static MidiUnitFragmentRenderPlan Clone(
        MidiUnitFragmentRenderPlan value,
        string? cacheKey,
        long cacheOffset,
        bool pcmCacheHit) => new(
            value.CanonicalZeroBasedPortNumber,
            value.CanonicalZeroBasedChannelNumber,
            value.TrackId,
            value.SegmentId,
            value.EventInstrumentId,
            value.InstanceGroupId,
            value.SubVoiceId,
            value.SourceIndex,
            value.StartFrame,
            value.EndFrame,
            value.SemanticFingerprint,
            value.Events,
            cacheKey,
            cacheOffset,
            pcmCacheHit,
            value.MidiChannelRootId,
            value.IsPercussion);

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

    internal static string ComputeNativeIdentity(string nativeDirectory)
    {
        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_BASS_NATIVE_BASELINE_WIN_X64_V1");
            foreach (string fileName in NativeFileNames)
            {
                writer.Write(fileName);
                writer.Write(HashFile(Path.Combine(nativeDirectory, fileName)));
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
    }

    internal static string HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static void ValidateSoundFontSetCacheIdentity(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The application SoundFont-set cache identity must be 64 lowercase hexadecimal characters.",
                nameof(value));
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
