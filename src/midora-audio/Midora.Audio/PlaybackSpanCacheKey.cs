using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public readonly record struct PlaybackSpanMasterSettings(
    float VolumeDecibels,
    float LimiterCeiling,
    float LimiterReleaseMilliseconds,
    bool LimiterEnabled);

public static class PlaybackSpanCacheKey
{
    public static string Create(
        MidiRenderPlan plan,
        string soundFontSetCacheIdentity,
        string nativeBaselineIdentity,
        int maximumSampleVoicesPerUnitStream,
        PlaybackSpanMasterSettings masterSettings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeBaselineIdentity);
        if (soundFontSetCacheIdentity.Length != 64
            || soundFontSetCacheIdentity.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The SoundFont-set cache identity must be a lowercase SHA-256 hexadecimal string.",
                nameof(soundFontSetCacheIdentity));
        }
        if (maximumSampleVoicesPerUnitStream is < 1 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_PLAYBACK_SPAN_CACHE_KEY_V9");
            writer.Write(6); // BASS renderer; every future peak constrains limiter attack.
            writer.Write(2); // Corrected look-ahead inter-sample limiter algorithm.
            writer.Write(plan.SampleRate);
            writer.Write(plan.TotalFrameCount);
            writer.Write(soundFontSetCacheIdentity);
            writer.Write(nativeBaselineIdentity);
            writer.Write(maximumSampleVoicesPerUnitStream);
            writer.Write(masterSettings.VolumeDecibels);
            writer.Write(masterSettings.LimiterCeiling);
            writer.Write(masterSettings.LimiterReleaseMilliseconds);
            writer.Write(masterSettings.LimiterEnabled);
            writer.Write(plan.SourceIds.Length);
            foreach (long sourceId in plan.SourceIds)
            {
                writer.Write(sourceId);
            }
            writer.Write(plan.InitiallyDisabledSourceIndices.Length);
            foreach (int sourceIndex in plan.InitiallyDisabledSourceIndices)
            {
                writer.Write(sourceIndex);
            }
            MidiUnitFragmentRenderPlan[] fragments = plan.UnitFragments.ToArray();
            writer.Write(fragments.Length);
            foreach (MidiUnitFragmentRenderPlan fragment in fragments
                .OrderBy(value => value.StartFrame)
                .ThenBy(value => value.TrackId)
                .ThenBy(value => value.SegmentId)
                .ThenBy(value => value.EventInstrumentId)
                .ThenBy(value => value.InstanceGroupId)
                .ThenBy(value => value.SubVoiceId))
            {
                // Physical Port/Channel allocation is deliberately excluded. The
                // renderer consumes each formal fragment as an abstract 1-channel
                // Unit, so route churn must not invalidate identical final PCM.
                writer.Write(fragment.TrackId);
                writer.Write(fragment.SegmentId);
                writer.Write(fragment.EventInstrumentId);
                writer.Write(fragment.InstanceGroupId);
                writer.Write(fragment.SubVoiceId);
                writer.Write(fragment.SourceIndex);
                writer.Write(fragment.StartFrame);
                writer.Write(fragment.EndFrame);
                writer.Write(fragment.SemanticFingerprint);
                writer.Write(fragment.Events.Length);
                foreach (ScheduledMidiMessage value in fragment.Events)
                {
                    writer.Write(value.SampleFrame);
                    writer.Write(value.Message.PackedValue);
                    writer.Write(value.SourceIndex);
                    WriteSystemExclusiveIdentity(writer, value);
                }
            }
            if (fragments.Length == 0)
            {
                // Playback-span retention is not enabled for draft preview plans,
                // but retain a complete deterministic fallback identity for callers.
                writer.Write(plan.Units.Length);
                foreach (MidiUnitRenderPlan unit in plan.Units)
                {
                    writer.Write(unit.CanonicalZeroBasedPortNumber);
                    writer.Write(unit.CanonicalZeroBasedChannelNumber);
                    writer.Write(unit.Events.Length);
                    foreach (ScheduledMidiMessage value in unit.Events)
                    {
                        writer.Write(value.SampleFrame);
                        writer.Write(value.Message.PackedValue);
                        writer.Write(value.SourceIndex);
                        WriteSystemExclusiveIdentity(writer, value);
                    }
                }
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(payload.GetBuffer().AsSpan(
            0,
            checked((int)payload.Length))));
    }

    private static void WriteSystemExclusiveIdentity(
        BinaryWriter writer,
        ScheduledMidiMessage value)
    {
        if (value.ChannelModeSystemExclusive is { } systemExclusive)
        {
            writer.Write((byte)systemExclusive.Kind);
            writer.Write(systemExclusive.DeviceId);
            writer.Write(systemExclusive.ModeValue);
        }
        else
        {
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
    }
}
