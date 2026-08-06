using System.Globalization;

namespace Midora.OutputPlanning;

public sealed record LogicalTrackOutputName(
    string SourceKey,
    int ProjectDisplayOrder,
    string? TrackName);

public static class InitialReleaseOutputNaming
{
    private const string ReadmeStem = "README";
    private const string WholeProjectMidiFallbackStem = "Midora MIDI Export";
    private const string WholeProjectAudioFallbackStem = "Midora Render";

    public const string ConductorTrackName = "Conductor";
    public const string ReadmeFileName = ReadmeStem + ".md";
    public const string WholeProjectMidiFallbackFileName = WholeProjectMidiFallbackStem + ".mid";
    public const string WholeProjectAudioFallbackFileName = WholeProjectAudioFallbackStem + ".wav";

    private const string ReadmeSourceKey = "readme";
    private const long ReadmeSourceOrder = long.MaxValue;

    public static OutputFileNamePlan PlanWholeProjectMidi(
        string? projectName,
        string? currentProjectFileStem,
        bool includeReadme)
    {
        List<OutputFileNameCandidate> candidates =
        [
            new(
                "whole-project-midi",
                0,
                SelectWholeProjectStem(
                    projectName,
                    currentProjectFileStem,
                    WholeProjectMidiFallbackStem),
                ".mid")
        ];
        AddReadmeIfRequested(candidates, includeReadme);
        return WindowsOutputFileNamePlanner.PlanSingleDirectory(candidates);
    }

    public static OutputFileNamePlan PlanWholeProjectAudio(
        string? projectName,
        string? currentProjectFileStem) =>
        WindowsOutputFileNamePlanner.PlanSingleDirectory(
        [
            new(
                "whole-project-audio",
                0,
                SelectWholeProjectStem(projectName, currentProjectFileStem, WholeProjectAudioFallbackStem),
                ".wav")
        ]);

    public static OutputFileNamePlan PlanLogicalTrackMidi(
        IEnumerable<LogicalTrackOutputName> selectedTracks,
        int totalProjectTrackCount,
        bool includeReadme) =>
        PlanLogicalTracks(selectedTracks, totalProjectTrackCount, ".mid", includeReadme);

    public static OutputFileNamePlan PlanLogicalTrackAudio(
        IEnumerable<LogicalTrackOutputName> selectedTracks,
        int totalProjectTrackCount) =>
        PlanLogicalTracks(selectedTracks, totalProjectTrackCount, ".wav", includeReadme: false);

    public static OutputFileNamePlan PlanPortMidi(
        IEnumerable<int> originalOneBasedPorts,
        bool includeReadme)
    {
        ArgumentNullException.ThrowIfNull(originalOneBasedPorts);

        List<OutputFileNameCandidate> candidates = [];
        foreach (int port in originalOneBasedPorts)
        {
            ValidatePort(port);
            candidates.Add(new(
                string.Create(CultureInfo.InvariantCulture, $"port:{port:D2}"),
                port,
                string.Create(CultureInfo.InvariantCulture, $"Port {port:D2}"),
                ".mid"));
        }
        AddReadmeIfRequested(candidates, includeReadme);
        return WindowsOutputFileNamePlanner.PlanSingleDirectory(candidates);
    }

    public static string GetLogicalTrackDisplayName(
        string? trackName,
        int projectDisplayOrder)
    {
        ValidateProjectDisplayOrder(projectDisplayOrder);
        return WindowsOutputFileNamePlanner.ClassifyCandidateStem(trackName)
            == OutputStemCandidateState.EmptyAfterLegalization
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Logical Track {projectDisplayOrder}")
                : trackName!;
    }

    public static string GetEventTrackName(
        string? logicalTrackName,
        int projectDisplayOrder,
        int originalOneBasedPort)
    {
        ValidatePort(originalOneBasedPort);
        string displayName = GetLogicalTrackDisplayName(logicalTrackName, projectDisplayOrder);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{displayName} / Port {originalOneBasedPort}");
    }

    private static OutputFileNamePlan PlanLogicalTracks(
        IEnumerable<LogicalTrackOutputName> selectedTracks,
        int totalProjectTrackCount,
        string extension,
        bool includeReadme)
    {
        ArgumentNullException.ThrowIfNull(selectedTracks);
        ArgumentOutOfRangeException.ThrowIfNegative(totalProjectTrackCount);

        int orderWidth = Math.Max(
            2,
            totalProjectTrackCount.ToString(CultureInfo.InvariantCulture).Length);
        List<OutputFileNameCandidate> candidates = [];
        foreach (LogicalTrackOutputName track in selectedTracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            if (string.IsNullOrEmpty(track.SourceKey))
            {
                throw new ArgumentException(
                    "Every logical track output name must have a non-empty stable source key.",
                    nameof(selectedTracks));
            }
            ValidateProjectDisplayOrder(track.ProjectDisplayOrder);
            if (track.ProjectDisplayOrder > totalProjectTrackCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(selectedTracks),
                    "A logical track display order cannot exceed the total Project track count.");
            }

            string label = GetLogicalTrackDisplayName(track.TrackName, track.ProjectDisplayOrder);
            string stem = string.Create(
                CultureInfo.InvariantCulture,
                $"{track.ProjectDisplayOrder.ToString($"D{orderWidth}", CultureInfo.InvariantCulture)} - {label}");
            candidates.Add(new(
                "logical-track:" + track.SourceKey,
                track.ProjectDisplayOrder,
                stem,
                extension));
        }
        AddReadmeIfRequested(candidates, includeReadme);
        return WindowsOutputFileNamePlanner.PlanSingleDirectory(candidates);
    }

    private static string SelectWholeProjectStem(
        string? projectName,
        string? currentProjectFileStem,
        string fallbackStem)
    {
        foreach (string? candidate in new[] { projectName, currentProjectFileStem })
        {
            if (WindowsOutputFileNamePlanner.ClassifyCandidateStem(candidate)
                != OutputStemCandidateState.EmptyAfterLegalization)
            {
                return candidate!;
            }
        }
        return fallbackStem;
    }

    private static void AddReadmeIfRequested(
        List<OutputFileNameCandidate> candidates,
        bool includeReadme)
    {
        if (includeReadme)
        {
            candidates.Add(new(ReadmeSourceKey, ReadmeSourceOrder, ReadmeStem, ".md"));
        }
    }

    private static void ValidateProjectDisplayOrder(int projectDisplayOrder) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(projectDisplayOrder, 1);

    private static void ValidatePort(int originalOneBasedPort)
    {
        if (originalOneBasedPort is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(originalOneBasedPort));
        }
    }
}
