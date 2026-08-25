using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public static class MidiUnitPcmCacheKey
{
    // v3 rejects PCM created from a default playback view that omitted the
    // otherwise-valid privileged Pure MIDI GS/XG channel-mode SysEx events.
    private const int RenderImplementationVersion = 3;

    public static string Create(
        MidiUnitFragmentRenderPlan fragment,
        int sampleRate,
        string soundFontSetCacheIdentity,
        string nativeBaselineIdentity,
        int maximumSampleVoicesPerUnitStream)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        ValidateSha256(soundFontSetCacheIdentity, nameof(soundFontSetCacheIdentity));
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeBaselineIdentity);
        if (maximumSampleVoicesPerUnitStream is < 1 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_UNIT_PCM_KEY_V4");
            writer.Write(RenderImplementationVersion);
            writer.Write(fragment.SemanticFingerprint);
            writer.Write(fragment.EndFrame - fragment.StartFrame);
            writer.Write(sampleRate);
            writer.Write(soundFontSetCacheIdentity);
            writer.Write(nativeBaselineIdentity);
            writer.Write(maximumSampleVoicesPerUnitStream);
            writer.Write(true); // BASS_MIDI_NOFX.
            writer.Write(true); // BASS_MIDI_NOTEOFF1.
            writer.Write(1f); // BASS_ATTRIB_MIDI_SRC: 8-point sinc.
            writer.Write(0f); // BASS_ATTRIB_MIDI_CPU.
            writer.Write(fragment.Events.Length);
            foreach (ScheduledMidiMessage value in fragment.Events)
            {
                writer.Write(value.SampleFrame - fragment.StartFrame);
                writer.Write(value.Message.PackedValue);
                WriteSystemExclusiveIdentity(writer, value);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
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

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64
            || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The content identity must be lowercase SHA-256 hexadecimal.",
                parameterName);
        }
    }
}
