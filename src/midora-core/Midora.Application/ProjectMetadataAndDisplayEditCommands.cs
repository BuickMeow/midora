using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateProjectMetadata(
        string projectName,
        string projectVersion,
        string authorOrTeam,
        string originalWork,
        string copyright) =>
        Command("Change project metadata", project =>
        {
            ProjectUserMetadata replacement = new(
                ProjectTextRules.ValidateShortTextContent(
                    projectName,
                    nameof(projectName)),
                ProjectTextRules.ValidateShortTextContent(
                    projectVersion,
                    nameof(projectVersion)),
                ProjectTextRules.ValidateMetadataText(
                    authorOrTeam,
                    nameof(authorOrTeam)),
                ProjectTextRules.ValidateMetadataText(
                    originalWork,
                    nameof(originalWork)),
                ProjectTextRules.ValidateMetadataText(
                    copyright,
                    nameof(copyright)));
            ProjectUserMetadata old = SnapshotUserMetadata(project.Metadata);
            return Prepared(
                old != replacement,
                NoCompilationChange(),
                _ => RestoreUserMetadata(project.Metadata, replacement),
                _ => RestoreUserMetadata(project.Metadata, old));
        });

    public static IProjectEditCommand UpdateLogicalTrackColorOverride(
        MidoraId trackId,
        MidoraColor? colorOverride) =>
        Command("Change logical track color", project =>
        {
            LogicalTrack track = FindLogicalTrack(project, trackId);
            MidoraColor? oldColor = track.ColorOverride;
            return Prepared(
                oldColor != colorOverride,
                TrackPresentationChange(trackId),
                _ => track.ColorOverride = colorOverride,
                _ => track.ColorOverride = oldColor);
        });

    private static ProjectUserMetadata SnapshotUserMetadata(ProjectMetadata value) =>
        new(
            value.ProjectName,
            value.ProjectVersion,
            value.AuthorOrTeam,
            value.OriginalWork,
            value.Copyright);

    private static LogicalTrack FindLogicalTrack(
        MidoraProject project,
        MidoraId trackId) =>
        project.Tracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    private static void RestoreUserMetadata(
        ProjectMetadata metadata,
        ProjectUserMetadata value)
    {
        metadata.ProjectName = value.ProjectName;
        metadata.ProjectVersion = value.ProjectVersion;
        metadata.AuthorOrTeam = value.AuthorOrTeam;
        metadata.OriginalWork = value.OriginalWork;
        metadata.Copyright = value.Copyright;
    }

    private readonly record struct ProjectUserMetadata(
        string ProjectName,
        string ProjectVersion,
        string AuthorOrTeam,
        string OriginalWork,
        string Copyright);
}
