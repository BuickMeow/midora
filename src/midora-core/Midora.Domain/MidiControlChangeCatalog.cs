namespace Midora.Domain;

public sealed record MidiControlChangeInfo(int Number, string Name)
{
    public string DisplayName => $"{Number} - {Name}";
}

/// <summary>
/// Frozen controller names from the BASSMIDI 2.4 MIDI implementation chart.
/// This is product data; runtime behavior never depends on network access.
/// </summary>
public static class MidiControlChangeCatalog
{
    private static readonly MidiControlChangeInfo[] RecognizedControllerArray =
    [
        new(0, "Bank Select (MSB)"),
        new(1, "Modulation Wheel (MSB)"),
        new(5, "Portamento Time (MSB)"),
        new(6, "Data Entry (MSB)"),
        new(7, "Channel Volume (MSB)"),
        new(10, "Pan (MSB)"),
        new(11, "Expression (MSB)"),
        new(32, "Bank Select (LSB)"),
        new(38, "Data Entry (LSB)"),
        new(42, "Pan (LSB)"),
        new(64, "Sustain Pedal"),
        new(65, "Portamento On/Off"),
        new(66, "Sostenuto"),
        new(67, "Soft Pedal"),
        new(71, "Sound Controller 2 (Filter Resonance)"),
        new(72, "Sound Controller 3 (Release Time)"),
        new(73, "Sound Controller 4 (Attack Time)"),
        new(74, "Sound Controller 5 (Filter Cutoff Frequency)"),
        new(75, "Sound Controller 6 (Decay Time)"),
        new(76, "Sound Controller 7 (Vibrato Rate)"),
        new(77, "Sound Controller 8 (Vibrato Depth)"),
        new(78, "Sound Controller 9 (Vibrato Delay)"),
        new(84, "Portamento Control"),
        new(91, "Effects 1 Depth (Reverb Send)"),
        new(93, "Effects 3 Depth (Chorus Send)"),
        new(94, "Effects 4 Depth (User Effect Send)"),
        new(98, "Non-Registered Parameter Number (LSB)"),
        new(99, "Non-Registered Parameter Number (MSB)"),
        new(100, "Registered Parameter Number (LSB)"),
        new(101, "Registered Parameter Number (MSB)"),
        new(120, "All Sound Off"),
        new(121, "Reset All Controllers"),
        new(123, "All Notes Off"),
        new(124, "Omni Mode Off"),
        new(125, "Omni Mode On"),
        new(126, "Poly Mode Off"),
        new(127, "Poly Mode On")
    ];

    private static readonly IReadOnlyDictionary<int, MidiControlChangeInfo> ByNumber =
        RecognizedControllerArray.ToDictionary(value => value.Number);

    public static IReadOnlyList<MidiControlChangeInfo> RecognizedControllers =>
        RecognizedControllerArray;

    public static IReadOnlyList<MidiControlChangeInfo> EditableControllers { get; } =
        RecognizedControllerArray
            .Where(value => value.Number <= 119 && value.Number is not 91 and not 93)
            .ToArray();

    public static bool IsEditable(int number) =>
        number <= 119 && number is not 91 and not 93 && ByNumber.ContainsKey(number);

    public static bool TryGet(int number, out MidiControlChangeInfo? info) =>
        ByNumber.TryGetValue(number, out info);

    public static string Format(int number) =>
        TryGet(number, out MidiControlChangeInfo? info)
            ? $"CC {info!.DisplayName}"
            : $"CC {number}";
}

public static class TemplateEventMidiTargets
{
    public static IEnumerable<MidiValueTarget> Enumerate(TemplateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (value.Kind)
        {
            case TemplateEventKind.ControlChange:
                yield return MidiValueTarget.ControlChange(value.Number);
                break;
            case TemplateEventKind.Bank:
                if (value.HasBankMsb) yield return MidiValueTarget.BankMsb;
                if (value.HasBankLsb) yield return MidiValueTarget.BankLsb;
                break;
            case TemplateEventKind.Program:
                yield return MidiValueTarget.Program;
                break;
            case TemplateEventKind.PitchBend:
                yield return MidiValueTarget.PitchBend;
                break;
            case TemplateEventKind.RegisteredParameter:
                yield return MidiValueTarget.Rpn(value.Number);
                break;
            case TemplateEventKind.NonRegisteredParameter:
                yield return MidiValueTarget.Nrpn(value.Number);
                break;
            case TemplateEventKind.PitchBendRange:
                yield return MidiValueTarget.PitchBendRangeSemitones;
                yield return MidiValueTarget.PitchBendRangeCents;
                break;
        }
    }

    public static int GetValue(TemplateEvent value, MidiValueTarget target)
    {
        if (!Enumerate(value).Contains(target))
        {
            throw new ArgumentException("The Template Event does not expose the requested MIDI target.", nameof(target));
        }
        return target.Kind switch
        {
            MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents => value.SecondaryValue,
            _ => value.Value
        };
    }

    public static string Format(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => MidiControlChangeCatalog.Format(target.Number),
        MidiValueKind.BankMsb => "Bank Select (MSB)",
        MidiValueKind.BankLsb => "Bank Select (LSB)",
        MidiValueKind.Program => "Program (1–128)",
        MidiValueKind.PitchBend => "Pitch Bend",
        MidiValueKind.RegisteredParameter => $"RPN {target.Number}",
        MidiValueKind.NonRegisteredParameter => $"NRPN {target.Number}",
        MidiValueKind.PitchBendRangeSemitones => "Pitch Bend Range (Semitones)",
        MidiValueKind.PitchBendRangeCents => "Pitch Bend Range (Cents)",
        _ => target.Kind.ToString()
    };
}
