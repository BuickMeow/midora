using NativeBassMidi = Midora.NativeInterops.BassMidi.BASSMIDI;

namespace Midora.Audio.Bass.Internals;

internal static class BassMidiSoundFontMapping
{
    public static NativeBassMidi.BASS_MIDI_FONTEX2 Create(
        uint handle,
        SoundFontConfiguration configuration)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        SoundFontTarget? target = configuration.Target;
        return new()
        {
            font = handle,
            spreset = target is null
                ? -1
                : configuration.IsSfz ? 0 : target.Value.Program,
            sbank = target is null
                ? -1
                : configuration.IsSfz ? 0 : target.Value.BankMsb,
            dpreset = target?.Program ?? -1,
            dbank = target?.BankMsb ?? 0,
            dbanklsb = target?.BankLsb ?? 0,
            minchan = 0,
            numchan = 0
        };
    }

    public static uint AddExtendedConfigurationFlag(int count)
    {
        if (count is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        return checked((uint)count) | NativeBassMidi.BASS_MIDI_FONT_EX2;
    }
}
