using Midora.Playback;

namespace Midora.Application;

public enum RecentDirectoryPurpose
{
    OpenProject,
    SaveAndSaveCopy,
    SoundFont,
    MidiExport,
    AudioRender
}

public sealed record RealtimeAudioPreferences(
    string? PlaybackOutputDeviceId,
    int RenderAheadMilliseconds,
    int DeviceBufferRequestMilliseconds,
    int MaximumSampleVoicesPerStream)
{
    public const int DefaultRenderAheadMilliseconds = 100;
    public const int DefaultDeviceBufferRequestMilliseconds = 50;
    public const int DefaultMaximumSampleVoicesPerStream = 750;
    public const int MinimumRenderAheadMilliseconds = 20;
    public const int MaximumRenderAheadMilliseconds = 2_000;
    public const int MinimumDeviceBufferRequestMilliseconds = 5;
    public const int MaximumDeviceBufferRequestMilliseconds = 200;
    public const int MinimumSampleVoicesPerStream = 1;
    public const int MaximumAllowedSampleVoicesPerStream = 16_777_216;

    public static RealtimeAudioPreferences Default { get; } = new(
        null,
        DefaultRenderAheadMilliseconds,
        DefaultDeviceBufferRequestMilliseconds,
        DefaultMaximumSampleVoicesPerStream);

    public void Validate()
    {
        if (PlaybackOutputDeviceId is { Length: 0 })
        {
            throw new ArgumentException(
                "The playback output device ID must be null for System Default or non-empty.",
                nameof(PlaybackOutputDeviceId));
        }
        if (RenderAheadMilliseconds is < MinimumRenderAheadMilliseconds
            or > MaximumRenderAheadMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(RenderAheadMilliseconds));
        }
        if (DeviceBufferRequestMilliseconds is < MinimumDeviceBufferRequestMilliseconds
            or > MaximumDeviceBufferRequestMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(DeviceBufferRequestMilliseconds));
        }
        if (MaximumSampleVoicesPerStream is < MinimumSampleVoicesPerStream
            or > MaximumAllowedSampleVoicesPerStream)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSampleVoicesPerStream));
        }
    }
}

public sealed record ApplicationRecentDirectories(
    string? OpenProject,
    string? SaveAndSaveCopy,
    string? SoundFont,
    string? MidiExport,
    string? AudioRender)
{
    public static ApplicationRecentDirectories Empty { get; } =
        new(null, null, null, null, null);

    public string? Get(RecentDirectoryPurpose purpose) => purpose switch
    {
        RecentDirectoryPurpose.OpenProject => OpenProject,
        RecentDirectoryPurpose.SaveAndSaveCopy => SaveAndSaveCopy,
        RecentDirectoryPurpose.SoundFont => SoundFont,
        RecentDirectoryPurpose.MidiExport => MidiExport,
        RecentDirectoryPurpose.AudioRender => AudioRender,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    internal ApplicationRecentDirectories With(
        RecentDirectoryPurpose purpose,
        string? directory) => purpose switch
        {
            RecentDirectoryPurpose.OpenProject => this with { OpenProject = directory },
            RecentDirectoryPurpose.SaveAndSaveCopy => this with { SaveAndSaveCopy = directory },
            RecentDirectoryPurpose.SoundFont => this with { SoundFont = directory },
            RecentDirectoryPurpose.MidiExport => this with { MidiExport = directory },
            RecentDirectoryPurpose.AudioRender => this with { AudioRender = directory },
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
}

public sealed record ApplicationPreferences(
    RealtimeAudioPreferences RealtimeAudio,
    ApplicationRecentDirectories RecentDirectories)
{
    public static ApplicationPreferences Default { get; } =
        new(RealtimeAudioPreferences.Default, ApplicationRecentDirectories.Empty);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(RealtimeAudio);
        ArgumentNullException.ThrowIfNull(RecentDirectories);
        RealtimeAudio.Validate();
        ValidateDirectory(RecentDirectories.OpenProject);
        ValidateDirectory(RecentDirectories.SaveAndSaveCopy);
        ValidateDirectory(RecentDirectories.SoundFont);
        ValidateDirectory(RecentDirectories.MidiExport);
        ValidateDirectory(RecentDirectories.AudioRender);
    }

    internal static string? NormalizeDirectory(string? directory)
    {
        if (directory is null)
        {
            return null;
        }
        ValidateDirectory(directory);
        string fullPath = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(fullPath)!;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void ValidateDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }
        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException(
                "A recent directory must be null or a fully-qualified non-empty path.",
                nameof(directory));
        }
        _ = Path.GetFullPath(directory);
    }
}

public enum ApplicationPreferenceUpdateStatus
{
    Applied,
    RejectedPlaybackNotStopped,
    RejectedApplicationBusy,
    RejectedInvalidValue,
    FailedUsingDefaults
}

public sealed record ApplicationPreferenceNotice(string Code, string Message, Exception? Error = null);

public sealed record ApplicationPreferenceUpdateResult(
    ApplicationPreferenceUpdateStatus Status,
    ApplicationPreferenceNotice? Notice = null)
{
    public bool Succeeded => Status == ApplicationPreferenceUpdateStatus.Applied;
}

public sealed class ApplicationPreferencesService
{
    private readonly object _sync = new();
    private readonly ApplicationPreferencesStore _store;
    private readonly ApplicationTaskCoordinator _tasks;
    private readonly ProjectCompilationSession _session;
    private ApplicationPreferences _current;

    public ApplicationPreferencesService(
        ApplicationPreferencesStore store,
        ApplicationTaskCoordinator tasks,
        ProjectCompilationSession session)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ApplicationPreferencesLoadResult loaded = _store.Load();
        _current = loaded.Preferences;
        StartupNotice = loaded.Notice;
    }

    public ApplicationPreferenceNotice? StartupNotice { get; }

    public ApplicationPreferences Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler? RealtimeAudioPreferencesChanged;

    public ApplicationPreferenceUpdateResult UpdateRealtimeAudio(
        RealtimeAudioPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            preferences.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with { RealtimeAudio = preferences },
            realtimeMayChange: true,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateRecentDirectory(
        RecentDirectoryPurpose purpose,
        string? directory)
    {
        string? normalized;
        try
        {
            normalized = ApplicationPreferences.NormalizeDirectory(directory);
        }
        catch (ArgumentException exception)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with
            {
                RecentDirectories = current.RecentDirectories.With(purpose, normalized)
            },
            realtimeMayChange: false,
            requiresPlaybackStopped: false);
    }

    public ApplicationPreferenceUpdateResult ResetToDefaults()
    {
        return Persist(
            _ => ApplicationPreferences.Default,
            realtimeMayChange: true,
            requiresPlaybackStopped: true);
    }

    private ApplicationPreferenceUpdateResult Persist(
        Func<ApplicationPreferences, ApplicationPreferences> update,
        bool realtimeMayChange,
        bool requiresPlaybackStopped)
    {
        IDisposable? admission = _tasks.TryAcquirePreferenceUpdateLock(
            requiresPlaybackStopped,
            out ApplicationPreferenceAdmissionFailure admissionFailure);
        if (admission is null)
        {
            return new(admissionFailure switch
            {
                ApplicationPreferenceAdmissionFailure.PlaybackNotStopped =>
                    ApplicationPreferenceUpdateStatus.RejectedPlaybackNotStopped,
                _ => ApplicationPreferenceUpdateStatus.RejectedApplicationBusy
            });
        }

        bool realtimeChanged;
        ApplicationPreferencesSaveResult saved;
        try
        {
            lock (_sync)
            {
                ApplicationPreferences candidate = update(_current);
                candidate.Validate();
                saved = _store.Save(candidate);
                if (saved.Succeeded)
                {
                    realtimeChanged = realtimeMayChange
                        && !Equals(_current.RealtimeAudio, candidate.RealtimeAudio);
                    _current = candidate;
                }
                else
                {
                    realtimeChanged = !Equals(
                        _current.RealtimeAudio,
                        ApplicationPreferences.Default.RealtimeAudio);
                    _current = ApplicationPreferences.Default;
                }
            }

            if (realtimeChanged)
            {
                _session.InvalidateSampleDomainCaches();
                RealtimeAudioPreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            return saved.Succeeded
                ? new(ApplicationPreferenceUpdateStatus.Applied)
                : new(ApplicationPreferenceUpdateStatus.FailedUsingDefaults, saved.Notice);
        }
        finally
        {
            admission.Dispose();
        }
    }
}
