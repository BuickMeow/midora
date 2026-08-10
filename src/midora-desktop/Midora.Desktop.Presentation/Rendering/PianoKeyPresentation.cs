using System.Globalization;

namespace Midora.Desktop.Presentation.Rendering;

public static class PianoKeyPresentation
{
    public static bool IsBlackKey(int midiNote)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(midiNote, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(midiNote, 127);
        int pitchClass = midiNote % 12;
        return pitchClass is 1 or 3 or 6 or 8 or 10;
    }

    public static string? GetOctaveCLabel(int midiNote)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(midiNote, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(midiNote, 127);
        return midiNote % 12 == 0
            ? "C" + (midiNote / 12 - 1).ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
