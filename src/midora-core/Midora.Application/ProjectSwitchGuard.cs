namespace Midora.Application;

public enum FunctionDraftResolution
{
    Apply,
    Discard,
    Cancel
}

public enum UnsavedProjectResolution
{
    SaveProject,
    CloseWithoutSaving,
    Cancel
}

public enum ProjectSwitchGuardStatus
{
    Completed,
    Cancelled,
    SaveUnavailable
}

public sealed class ProjectSwitchGuardResult<T>
{
    private ProjectSwitchGuardResult(ProjectSwitchGuardStatus status, T? value)
    {
        Status = status;
        Value = value;
    }

    public ProjectSwitchGuardStatus Status { get; }
    public T? Value { get; }

    internal static ProjectSwitchGuardResult<T> Completed(T value) =>
        new(ProjectSwitchGuardStatus.Completed, value);

    internal static ProjectSwitchGuardResult<T> Cancelled() =>
        new(ProjectSwitchGuardStatus.Cancelled, default);

    internal static ProjectSwitchGuardResult<T> SaveUnavailable() =>
        new(ProjectSwitchGuardStatus.SaveUnavailable, default);
}

public interface IProjectSwitchGuardActions<T>
{
    bool HasFunctionDrafts { get; }
    bool IsProjectModified { get; }
    bool CanSaveProject { get; }

    ValueTask<FunctionDraftResolution> ResolveFunctionDraftsAsync(CancellationToken cancellationToken);
    Task ApplyFunctionDraftsAsync(CancellationToken cancellationToken);
    Task DiscardFunctionDraftsAsync(CancellationToken cancellationToken);
    ValueTask<UnsavedProjectResolution> ResolveUnsavedProjectAsync(CancellationToken cancellationToken);
    Task SaveProjectAsync(CancellationToken cancellationToken);
    Task<T> PerformProjectSwitchAsync(CancellationToken cancellationToken);
}
