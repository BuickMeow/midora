using Midora.Audio;
using Midora.Compiler;
using System.Globalization;
using System.Text;

namespace Midora.Playback;

public sealed record AudioSynthesisCacheEnvironment(
    int SampleRate,
    string SoundFontSetCacheIdentity,
    string NativeBaselineIdentity,
    int MaximumSampleVoicesPerUnitStream)
{
    public void Validate()
    {
        if (SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate));
        }
        if (SoundFontSetCacheIdentity.Length != 64
            || SoundFontSetCacheIdentity.Any(value => value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The SoundFont-set cache identity must be a lowercase SHA-256 hexadecimal string.",
                nameof(SoundFontSetCacheIdentity));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(NativeBaselineIdentity);
        if (MaximumSampleVoicesPerUnitStream is < 1 or > 16_777_216)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSampleVoicesPerUnitStream));
        }
    }
}

public static class AudioUnitPcmCacheKey
{
    // v3 preserves privileged Pure MIDI GS/XG channel-mode SysEx when a full
    // canonical result is wrapped as the default playback view. v2 entries may
    // have been rendered from a view that silently omitted those events.
    private const int AudioRenderImplementationVersion = 3;

    public static string Create(
        CanonicalAudioUnitFragment fragment,
        CanonicalCompiledResult compiled,
        AudioSynthesisCacheEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(environment);
        environment.Validate();
        if (!compiled.IsConsumable || compiled.IsPartial)
        {
            throw new ArgumentException(
                "Only a consumable canonical result can produce an audio cache key.",
                nameof(compiled));
        }
        if (fragment.EffectiveStartTick < compiled.StartTick
            || fragment.EffectiveEndTick > compiled.EndTick
            || fragment.EffectiveEndTick < fragment.EffectiveStartTick)
        {
            throw new ArgumentException(
                "The Unit fragment is outside the canonical compilation range.",
                nameof(fragment));
        }

        using MemoryStream payload = new();
        using (BinaryWriter writer = new(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MIDORA_UNIT_PCM_CACHE_KEY_V4");
            writer.Write(AudioRenderImplementationVersion);
            writer.Write(fragment.SemanticFingerprint);
            writer.Write(compiled.TicksPerQuarterNote);
            writer.Write(environment.SampleRate);
            writer.Write(environment.SoundFontSetCacheIdentity);
            writer.Write(environment.NativeBaselineIdentity);
            writer.Write(environment.MaximumSampleVoicesPerUnitStream);
            writer.Write(true); // BASS_MIDI_NOFX
            writer.Write(true); // BASS_MIDI_NOTEOFF1
            writer.Write(1f); // BASS_ATTRIB_MIDI_SRC: 8-point sinc.
            writer.Write(0f); // BASS_ATTRIB_MIDI_CPU.
            WriteTempoProjection(writer, fragment, compiled.Tempos);
        }
        return AudioCacheSessionStore.ComputeKey(payload.GetBuffer().AsSpan(
            0,
            checked((int)payload.Length)));
    }

    private static void WriteTempoProjection(
        BinaryWriter writer,
        CanonicalAudioUnitFragment fragment,
        ReadOnlySpan<CanonicalTempo> tempos)
    {
        CanonicalTempo? active = null;
        List<CanonicalTempo> changes = [];
        foreach (CanonicalTempo tempo in tempos)
        {
            if (tempo.Tick <= fragment.EffectiveStartTick)
            {
                active = tempo;
            }
            else if (tempo.Tick < fragment.EffectiveEndTick)
            {
                changes.Add(tempo);
            }
        }
        if (active is null)
        {
            throw new InvalidDataException(
                "The canonical conductor has no effective Tempo at the Unit fragment start.");
        }

        writer.Write(changes.Count + 1);
        writer.Write(0L);
        writer.Write(active.Value.BeatsPerMinute.ToString(CultureInfo.InvariantCulture));
        foreach (CanonicalTempo tempo in changes)
        {
            writer.Write(tempo.Tick - fragment.EffectiveStartTick);
            writer.Write(tempo.BeatsPerMinute.ToString(CultureInfo.InvariantCulture));
        }
    }
}
