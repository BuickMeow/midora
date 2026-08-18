using Midora.Domain;

namespace Midora.Application;

internal static class TemplateEventExactCollision
{
    private const int BankMsb = 1;
    private const int BankLsb = 2;
    private const int Program = 3;
    private const int PitchBend = 4;
    private const int PitchBendRangeOrRpnZero = 5;
    private const int ControlChangeBase = 1_000;
    private const int RpnBase = 2_000;
    private const int NrpnBase = 20_000;
    private const int OtherBase = 40_000;

    public static int[] GetNonNoteDetails(
        TemplateEventKind kind,
        int number,
        bool hasBankMsb,
        bool hasBankLsb) => kind switch
    {
        TemplateEventKind.ControlChange => [checked(ControlChangeBase + number)],
        TemplateEventKind.Bank => BankDetails(hasBankMsb, hasBankLsb),
        TemplateEventKind.Program => [Program],
        TemplateEventKind.PitchBend => [PitchBend],
        TemplateEventKind.RegisteredParameter when number == 0 => [PitchBendRangeOrRpnZero],
        TemplateEventKind.RegisteredParameter => [checked(RpnBase + number)],
        TemplateEventKind.NonRegisteredParameter => [checked(NrpnBase + number)],
        TemplateEventKind.PitchBendRange => [PitchBendRangeOrRpnZero],
        _ => [checked(OtherBase + (int)kind)]
    };

    public static bool Conflicts(
        TemplateEventKind leftKind,
        int leftNumber,
        bool leftHasBankMsb,
        bool leftHasBankLsb,
        TemplateEventKind rightKind,
        int rightNumber,
        bool rightHasBankMsb,
        bool rightHasBankLsb)
    {
        if (leftKind == TemplateEventKind.Note || rightKind == TemplateEventKind.Note)
        {
            return false;
        }
        int[] left = GetNonNoteDetails(
            leftKind,
            leftNumber,
            leftHasBankMsb,
            leftHasBankLsb);
        int[] right = GetNonNoteDetails(
            rightKind,
            rightNumber,
            rightHasBankMsb,
            rightHasBankLsb);
        return left.Any(right.Contains);
    }

    private static int[] BankDetails(bool hasBankMsb, bool hasBankLsb) =>
        (hasBankMsb, hasBankLsb) switch
        {
            (true, true) => [BankMsb, BankLsb],
            (true, false) => [BankMsb],
            (false, true) => [BankLsb],
            _ => []
        };
}

public static partial class ProjectDomainEditCommands
{
    private static void RemoveLaterExactTimelineCollisions(EventInstrument instrument)
    {
        foreach (SubVoice voice in instrument.SubVoices)
        {
            RemoveLaterExactTimelineCollisions(voice);
        }
    }

    private static void RemoveLaterExactTimelineCollisions(SubVoice voice)
    {
        HashSet<(bool Note, long Tick, int Detail)> occupied = [];
        for (int index = 0; index < voice.Events.Count;)
        {
            TemplateEvent value = voice.Events[index];
            (bool Note, long Tick, int Detail)[] keys = value.Kind == TemplateEventKind.Note
                ? [(true, value.Tick, value.Number)]
                : TemplateEventExactCollision.GetNonNoteDetails(
                        value.Kind,
                        value.Number,
                        value.HasBankMsb,
                        value.HasBankLsb)
                    .Select(detail => (false, value.Tick, detail))
                    .ToArray();
            if (keys.Any(occupied.Contains))
            {
                voice.Events.RemoveAt(index);
                continue;
            }
            occupied.UnionWith(keys);
            index++;
        }

        foreach (ValueCurve curve in voice.Curves)
        {
            HashSet<long> pointTicks = [];
            curve.Points.RemoveAll(point => !pointTicks.Add(point.Tick));
        }
    }
}
