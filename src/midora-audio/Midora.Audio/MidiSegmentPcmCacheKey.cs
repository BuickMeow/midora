using System.Security.Cryptography;
using System.Text;

namespace Midora.Audio;

public static class MidiSegmentPcmCacheKey
{
    private const int RenderImplementationVersion = 1;

    public static string Create(
        MidiSegmentRenderPlan segment,
        int sampleRate,
        string soundFontSha256,
        string nativeBaselineIdentity,
        int maximumSampleVoicesPerUnitStream)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
        ValidateSha256(soundFontSha256, nameof(soundFontSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeBaselineIdentity);
        if (maximumSampleVoicesPerUnitStream is < 1 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleVoicesPerUnitStream));
        }

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_SAMPLE_DOMAIN_SEGMENT_PCM_KEY_V1");
            writer.Write(RenderImplementationVersion);
            writer.Write(segment.SemanticFingerprint);
            writer.Write(segment.FrameCount);
            writer.Write(sampleRate);
            writer.Write(soundFontSha256);
            writer.Write(nativeBaselineIdentity);
            writer.Write(maximumSampleVoicesPerUnitStream);
            writer.Write(true); // BASS_MIDI_NOFX.
            writer.Write(true); // BASS_MIDI_NOTEOFF1.
            writer.Write(1f); // BASS_ATTRIB_MIDI_SRC: 8-point sinc.
            writer.Write(0f); // BASS_ATTRIB_MIDI_CPU.
            writer.Write(16_384); // Stable physical block size; not logical identity granularity.
            writer.Write(2); // stereo.
            writer.Write((int)AudioDevice.AudioSampleFormat.Float32);
        }
        return Convert.ToHexStringLower(SHA256.HashData(
            payload.GetBuffer().AsSpan(0, checked((int)payload.Length))));
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
