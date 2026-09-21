namespace Midora.Application;

/// <summary>
/// Atomic, fail-closed publish of a store file over its previous revision.
/// </summary>
/// <remarks>
/// Windows enforces exclusive access through file sharing modes, so its replace operation already
/// fails when another reader or writer holds the target. Unix renames ignore advisory locks, so the
/// target is additionally held exclusively for the duration of the replacement; a concurrent
/// Midora writer then fails closed instead of silently losing an update. On both platforms the
/// previous revision stays intact whenever the replacement fails.
/// </remarks>
internal static class AtomicStorePublish
{
    public static void ReplaceExisting(string temporaryPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (OperatingSystem.IsWindows())
        {
            File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
            return;
        }

        using FileStream guard = new(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);
    }
}
