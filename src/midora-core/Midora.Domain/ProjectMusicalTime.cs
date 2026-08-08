using System.Globalization;

namespace Midora.Domain;

public static class ProjectTimeSignatureRules
{
    public static bool IsSupportedDenominator(int denominator) =>
        denominator is 1 or 2 or 4 or 8 or 16 or 32 or 64;

    public static bool IsCompatible(int ticksPerQuarterNote, int denominator) =>
        ticksPerQuarterNote is >= MidoraProject.MinimumTicksPerQuarterNote
            and <= MidoraProject.MaximumTicksPerQuarterNote
        && IsSupportedDenominator(denominator)
        && checked(4L * ticksPerQuarterNote) % denominator == 0;

    public static int GetTicksPerBeat(int ticksPerQuarterNote, int denominator)
    {
        ValidateCompatibility(ticksPerQuarterNote, denominator, nameof(denominator));
        return checked((int)(4L * ticksPerQuarterNote / denominator));
    }

    public static long GetTicksPerBar(
        int ticksPerQuarterNote,
        int numerator,
        int denominator)
    {
        if (numerator is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }
        return checked((long)numerator * GetTicksPerBeat(ticksPerQuarterNote, denominator));
    }

    public static void ValidateCompatibility(
        int ticksPerQuarterNote,
        int denominator,
        string? parameterName = null)
    {
        if (ticksPerQuarterNote is < MidoraProject.MinimumTicksPerQuarterNote
            or > MidoraProject.MaximumTicksPerQuarterNote)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        if (!IsSupportedDenominator(denominator))
        {
            throw new ArgumentOutOfRangeException(parameterName ?? nameof(denominator));
        }
        if (checked(4L * ticksPerQuarterNote) % denominator != 0)
        {
            throw new ArgumentException(
                $"Time Signature denominator {denominator} is incompatible with TPQ "
                + $"{ticksPerQuarterNote}; 4 * TPQ must be divisible by the denominator.",
                parameterName ?? nameof(denominator));
        }
    }
}

public readonly record struct ProjectMusicalPosition
{
    public ProjectMusicalPosition(ulong bar, int beat, int tickOffset)
    {
        if (bar == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bar));
        }
        if (beat <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beat));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(tickOffset);
        Bar = bar;
        Beat = beat;
        TickOffset = tickOffset;
    }

    public ulong Bar { get; }
    public int Beat { get; }
    public int TickOffset { get; }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Bar}:{Beat}:{TickOffset}");

    public static ProjectMusicalPosition Parse(string value)
    {
        if (!TryParse(value, out ProjectMusicalPosition result))
        {
            throw new FormatException("Project musical position must use Bar:Beat:Tick.");
        }
        return result;
    }

    public static bool TryParse(string? value, out ProjectMusicalPosition result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }
        string[] parts = value.Split(':');
        if (parts.Length != 3
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong bar)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int beat)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int tickOffset)
            || bar == 0
            || beat <= 0
            || tickOffset < 0)
        {
            return false;
        }
        result = new(bar, beat, tickOffset);
        return true;
    }
}

public readonly record struct ProjectBarInfo(
    ulong Bar,
    long StartTick,
    long EndTick,
    int Numerator,
    int Denominator,
    int TicksPerBeat,
    bool IsTruncatedByTimeSignatureChange);

public readonly record struct ProjectTimeSignaturePoint(
    MidoraId Id,
    long Tick,
    int Numerator,
    int Denominator);

public sealed class ProjectTimeSignatureMap
{
    private readonly Entry[] _entries;

    public ProjectTimeSignatureMap(MidoraProject project)
        : this(
            (project ?? throw new ArgumentNullException(nameof(project))).TicksPerQuarterNote,
            project.Conductor.TimeSignatures.Select(value => new ProjectTimeSignaturePoint(
                value.Id,
                value.Tick,
                value.Numerator,
                value.Denominator)))
    {
    }

    public ProjectTimeSignatureMap(
        int ticksPerQuarterNote,
        IEnumerable<TimeSignatureChange> timeSignatures)
        : this(
            ticksPerQuarterNote,
            (timeSignatures ?? throw new ArgumentNullException(nameof(timeSignatures)))
                .Select(value => new ProjectTimeSignaturePoint(
                    value.Id,
                    value.Tick,
                    value.Numerator,
                    value.Denominator)))
    {
    }

    public ProjectTimeSignatureMap(
        int ticksPerQuarterNote,
        IEnumerable<ProjectTimeSignaturePoint> timeSignatures)
    {
        if (ticksPerQuarterNote is < MidoraProject.MinimumTicksPerQuarterNote
            or > MidoraProject.MaximumTicksPerQuarterNote)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        ArgumentNullException.ThrowIfNull(timeSignatures);
        TicksPerQuarterNote = ticksPerQuarterNote;
        ProjectTimeSignaturePoint[] ordered = timeSignatures
            .OrderBy(value => value.Tick)
            .ThenBy(value => value.Id)
            .ToArray();
        if (ordered.Length == 0 || ordered[0].Tick != 0)
        {
            throw new ArgumentException(
                "The Time Signature map must contain exactly one change at tick 0.",
                nameof(timeSignatures));
        }

        _entries = new Entry[ordered.Length];
        HashSet<long> ticks = [];
        ulong startBar = 1;
        for (int i = 0; i < ordered.Length; i++)
        {
            ProjectTimeSignaturePoint value = ordered[i];
            if (value.Tick < 0 || !ticks.Add(value.Tick))
            {
                throw new ArgumentException(
                    "Time Signature ticks must be non-negative and unique.",
                    nameof(timeSignatures));
            }
            int ticksPerBeat = ProjectTimeSignatureRules.GetTicksPerBeat(
                ticksPerQuarterNote,
                value.Denominator);
            long ticksPerBar = ProjectTimeSignatureRules.GetTicksPerBar(
                ticksPerQuarterNote,
                value.Numerator,
                value.Denominator);
            if (i > 0)
            {
                Entry previous = _entries[i - 1];
                long delta = checked(value.Tick - previous.StartTick);
                ulong bars = checked((ulong)(delta / previous.TicksPerBar));
                if (delta % previous.TicksPerBar != 0)
                {
                    bars = checked(bars + 1);
                }
                startBar = checked(previous.StartBar + bars);
            }
            _entries[i] = new(
                value.Id,
                value.Tick,
                startBar,
                value.Numerator,
                value.Denominator,
                ticksPerBeat,
                ticksPerBar);
        }
    }

    public int TicksPerQuarterNote { get; }

    public ProjectMusicalPosition GetPosition(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        Entry entry = _entries[FindEntryForTick(tick)];
        long localTick = tick - entry.StartTick;
        ulong bar = checked(entry.StartBar + (ulong)(localTick / entry.TicksPerBar));
        long tickInBar = localTick % entry.TicksPerBar;
        int beat = checked((int)(tickInBar / entry.TicksPerBeat) + 1);
        int tickOffset = checked((int)(tickInBar % entry.TicksPerBeat));
        return new(bar, beat, tickOffset);
    }

    public long GetTick(ProjectMusicalPosition position)
    {
        if (position.Bar == 0 || position.Beat <= 0 || position.TickOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        int index = FindEntryForBar(position.Bar);
        Entry entry = _entries[index];
        if (position.Beat > entry.Numerator || position.TickOffset >= entry.TicksPerBeat)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        ulong localBars = position.Bar - entry.StartBar;
        ulong localTick = checked(
            localBars * (ulong)entry.TicksPerBar
            + (ulong)(position.Beat - 1) * (ulong)entry.TicksPerBeat
            + (ulong)position.TickOffset);
        ulong absoluteTick = checked((ulong)entry.StartTick + localTick);
        if (absoluteTick > long.MaxValue)
        {
            throw new OverflowException("The musical position exceeds the Project tick domain.");
        }
        long result = (long)absoluteTick;
        if (index + 1 < _entries.Length && result >= _entries[index + 1].StartTick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "The musical position lies in the truncated tail of a previous bar.");
        }
        return result;
    }

    public ProjectBarInfo GetBarContaining(long tick)
    {
        ProjectMusicalPosition position = GetPosition(tick);
        int index = FindEntryForBar(position.Bar);
        Entry entry = _entries[index];
        long startTick = GetTick(new(position.Bar, 1, 0));
        long naturalEnd = checked(startTick + entry.TicksPerBar);
        long endTick = naturalEnd;
        bool truncated = false;
        if (index + 1 < _entries.Length && _entries[index + 1].StartTick < naturalEnd)
        {
            endTick = _entries[index + 1].StartTick;
            truncated = true;
        }
        return new(
            position.Bar,
            startTick,
            endTick,
            entry.Numerator,
            entry.Denominator,
            entry.TicksPerBeat,
            truncated);
    }

    public long GetBeatGridTickAtOrBefore(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        Entry entry = _entries[FindEntryForTick(tick)];
        long localTick = tick - entry.StartTick;
        return checked(entry.StartTick + localTick / entry.TicksPerBeat * entry.TicksPerBeat);
    }

    public long GetBeatGridTickAtOrAfter(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        int index = FindEntryForTick(tick);
        Entry entry = _entries[index];
        long localTick = tick - entry.StartTick;
        long remainder = localTick % entry.TicksPerBeat;
        if (remainder == 0)
        {
            return tick;
        }
        long candidate = checked(tick + entry.TicksPerBeat - remainder);
        return index + 1 < _entries.Length && candidate > _entries[index + 1].StartTick
            ? _entries[index + 1].StartTick
            : candidate;
    }

    public long SnapToNearestBeatGrid(long tick)
    {
        long before = GetBeatGridTickAtOrBefore(tick);
        long after = GetBeatGridTickAtOrAfter(tick);
        return tick - before < after - tick ? before : after;
    }

    private int FindEntryForTick(long tick)
    {
        int low = 0;
        int high = _entries.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_entries[middle].StartTick <= tick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return high;
    }

    private int FindEntryForBar(ulong bar)
    {
        int low = 0;
        int high = _entries.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_entries[middle].StartBar <= bar)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return high;
    }

    private readonly record struct Entry(
        MidoraId SourceId,
        long StartTick,
        ulong StartBar,
        int Numerator,
        int Denominator,
        int TicksPerBeat,
        long TicksPerBar);
}
