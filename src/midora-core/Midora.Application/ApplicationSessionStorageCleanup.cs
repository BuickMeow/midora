using Midora.Audio;
using Midora.Persistence;

namespace Midora.Application;

public readonly record struct ApplicationSessionStorageCleanupResult(
    int AudioCacheDirectoryCount,
    int SessionContentDirectoryCount);

public static class ApplicationSessionStorageCleanup
{
    public static ApplicationSessionStorageCleanupResult ClearInactiveSessions(
        ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        int audioCacheDirectories = AudioCacheSessionStore.ClearInactiveSessions(
            preferences.AudioCache.RootPath);
        int sessionContentDirectories =
            SessionContentDirectoryLease.ClearDefaultInactiveDirectories();
        return new(audioCacheDirectories, sessionContentDirectories);
    }
}
