namespace Midora.Domain;

public sealed record ProjectMetadataSnapshot(
    string ProjectName,
    string ProjectVersion,
    string AuthorOrTeam,
    string OriginalWork,
    string Copyright,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ModifiedAtUtc,
    long TotalEditingTimeMilliseconds);

public sealed class ProjectMetadata
{
    private int _editingTimeSessionActive;

    internal ProjectMetadata(DateTimeOffset createdAtUtc)
    {
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        ModifiedAtUtc = CreatedAtUtc;
    }

    public string ProjectName { get; set; } = string.Empty;
    public string ProjectVersion { get; set; } = string.Empty;
    public string AuthorOrTeam { get; set; } = string.Empty;
    public string OriginalWork { get; set; } = string.Empty;
    public string Copyright { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ModifiedAtUtc { get; private set; }
    public long TotalEditingTimeMilliseconds { get; private set; }

    public ProjectMetadataSnapshot Snapshot() => new(
        ProjectName,
        ProjectVersion,
        AuthorOrTeam,
        OriginalWork,
        Copyright,
        CreatedAtUtc,
        ModifiedAtUtc,
        TotalEditingTimeMilliseconds);

    internal void Restore(ProjectMetadataSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Volatile.Read(ref _editingTimeSessionActive) != 0)
        {
            throw new InvalidOperationException(
                "Project metadata cannot be restored while its editing-time session is active.");
        }
        ArgumentNullException.ThrowIfNull(snapshot.ProjectName);
        ArgumentNullException.ThrowIfNull(snapshot.ProjectVersion);
        ArgumentNullException.ThrowIfNull(snapshot.AuthorOrTeam);
        ArgumentNullException.ThrowIfNull(snapshot.OriginalWork);
        ArgumentNullException.ThrowIfNull(snapshot.Copyright);
        if (snapshot.TotalEditingTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        }

        DateTimeOffset createdAtUtc = snapshot.CreatedAtUtc.ToUniversalTime();
        DateTimeOffset modifiedAtUtc = snapshot.ModifiedAtUtc.ToUniversalTime();
        if (modifiedAtUtc < createdAtUtc)
        {
            throw new ArgumentException(
                "Project modified time cannot precede its creation time.",
                nameof(snapshot));
        }

        ProjectName = snapshot.ProjectName;
        ProjectVersion = snapshot.ProjectVersion;
        AuthorOrTeam = snapshot.AuthorOrTeam;
        OriginalWork = snapshot.OriginalWork;
        Copyright = snapshot.Copyright;
        CreatedAtUtc = createdAtUtc;
        ModifiedAtUtc = modifiedAtUtc;
        TotalEditingTimeMilliseconds = snapshot.TotalEditingTimeMilliseconds;
    }

    internal void SetTotalEditingTimeMilliseconds(long value)
    {
        if (value < TotalEditingTimeMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Accumulated Project time cannot decrease.");
        }
        TotalEditingTimeMilliseconds = value;
    }

    internal void CommitSuccessfulSave(DateTimeOffset modifiedAtUtc)
    {
        DateTimeOffset value = modifiedAtUtc.ToUniversalTime();
        if (value < CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiedAtUtc));
        }
        ModifiedAtUtc = value;
    }

    internal void BeginEditingTimeSession()
    {
        if (Interlocked.CompareExchange(ref _editingTimeSessionActive, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one editing-time session may own an open Project.");
        }
    }

    internal void EndEditingTimeSession() =>
        Volatile.Write(ref _editingTimeSessionActive, 0);
}
