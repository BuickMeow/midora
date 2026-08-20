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
    int MaximumSampleVoicesPerUnitStream)
{
    public const int DefaultRenderAheadMilliseconds = 100;
    public const int DefaultDeviceBufferRequestMilliseconds = 50;
    public const int DefaultMaximumSampleVoicesPerUnitStream = 500;
    public const int MinimumRenderAheadMilliseconds = 20;
    public const int MaximumRenderAheadMilliseconds = 2_000;
    public const int MinimumDeviceBufferRequestMilliseconds = 5;
    public const int MaximumDeviceBufferRequestMilliseconds = 200;
    public const int MinimumSampleVoicesPerUnitStream = 1;
    public const int MaximumAllowedSampleVoicesPerUnitStream = 16_777_216;

    public static RealtimeAudioPreferences Default { get; } = new(
        null,
        DefaultRenderAheadMilliseconds,
        DefaultDeviceBufferRequestMilliseconds,
        DefaultMaximumSampleVoicesPerUnitStream);

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
        if (MaximumSampleVoicesPerUnitStream is < MinimumSampleVoicesPerUnitStream
            or > MaximumAllowedSampleVoicesPerUnitStream)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSampleVoicesPerUnitStream));
        }
    }
}

public sealed record AudioCachePreferences(
    string RootPath,
    long MaximumReusableBytes)
{
    public const long DefaultMaximumReusableBytes = 16L * 1024 * 1024 * 1024;

    public static AudioCachePreferences Default { get; } = new(
        GetDefaultRootPath(),
        DefaultMaximumReusableBytes);

    public static string GetDefaultRootPath()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The current Windows user's Local Application Data directory is unavailable.");
        }
        return Path.Combine(localApplicationData, "Midora", "AudioCache");
    }

    public void Validate()
    {
        if (MaximumReusableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReusableBytes));
        }
        _ = NormalizeRootPath(RootPath);
    }

    public AudioCachePreferences Normalize() => this with
    {
        RootPath = NormalizeRootPath(RootPath)
    };

    internal static string NormalizeRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath)
            || rootPath.StartsWith("\\\\", StringComparison.Ordinal)
            || rootPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The audio cache root must be a fully-qualified local path; UNC and network paths are not supported.",
                nameof(rootPath));
        }

        string fullPath = Path.GetFullPath(rootPath);
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw new ArgumentException("The audio cache root has no local volume root.", nameof(rootPath));
        }
        return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
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

public sealed record DesktopUiPreferences(
    double MainWindowWidth,
    double MainWindowHeight,
    double? MainWindowLeft,
    double? MainWindowTop,
    bool MainWindowMaximized,
    double ProjectPanelWidth,
    double InspectorWidth,
    double BottomPanelHeight,
    bool TimelineSnapEnabled,
    int TimelineGridDivisionsPerQuarter)
{
    public bool ProjectPanelVisible { get; init; } = true;
    public bool InspectorVisible { get; init; } = true;
    public bool BottomPanelVisible { get; init; } = true;
    public bool FollowPlayback { get; init; } = true;

    public static DesktopUiPreferences Default { get; } = new(
        1440,
        900,
        null,
        null,
        false,
        224,
        270,
        150,
        true,
        4);

    public void Validate()
    {
        if (!double.IsFinite(MainWindowWidth) || MainWindowWidth is < 1100 or > 32768
            || !double.IsFinite(MainWindowHeight) || MainWindowHeight is < 680 or > 32768
            || MainWindowLeft.HasValue && !double.IsFinite(MainWindowLeft.Value)
            || MainWindowTop.HasValue && !double.IsFinite(MainWindowTop.Value)
            || !double.IsFinite(ProjectPanelWidth) || ProjectPanelWidth is < 170 or > 360
            || !double.IsFinite(InspectorWidth) || InspectorWidth is < 220 or > 420
            || !double.IsFinite(BottomPanelHeight) || BottomPanelHeight is < 80 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(DesktopUiPreferences));
        }
        if (TimelineGridDivisionsPerQuarter is not (1 or 2 or 3 or 4 or 6 or 8 or 12 or 16 or 24 or 32 or 48 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(TimelineGridDivisionsPerQuarter));
        }
    }
}

public sealed record ApplicationPreferences(
    RealtimeAudioPreferences RealtimeAudio,
    AudioCachePreferences AudioCache,
    ApplicationRecentDirectories RecentDirectories)
{
    public DesktopUiPreferences DesktopUi { get; init; } = DesktopUiPreferences.Default;
    public string? DefaultEmbeddedSoundFontPath { get; init; }

    public static ApplicationPreferences Default { get; } =
        new(
            RealtimeAudioPreferences.Default,
            AudioCachePreferences.Default,
            ApplicationRecentDirectories.Empty);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(RealtimeAudio);
        ArgumentNullException.ThrowIfNull(AudioCache);
        ArgumentNullException.ThrowIfNull(RecentDirectories);
        ArgumentNullException.ThrowIfNull(DesktopUi);
        RealtimeAudio.Validate();
        AudioCache.Validate();
        DesktopUi.Validate();
        ValidateDirectory(RecentDirectories.OpenProject);
        ValidateDirectory(RecentDirectories.SaveAndSaveCopy);
        ValidateDirectory(RecentDirectories.SoundFont);
        ValidateDirectory(RecentDirectories.MidiExport);
        ValidateDirectory(RecentDirectories.AudioRender);
        ValidateOptionalLocalFilePath(
            DefaultEmbeddedSoundFontPath,
            nameof(DefaultEmbeddedSoundFontPath));
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

    internal static string? NormalizeOptionalLocalFilePath(string? path)
    {
        if (path is null)
        {
            return null;
        }
        ValidateOptionalLocalFilePath(path, nameof(path));
        return Path.GetFullPath(path);
    }

    private static void ValidateOptionalLocalFilePath(string? path, string parameterName)
    {
        if (path is null)
        {
            return;
        }
        if (path.Length == 0
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The default embedded SoundFont must be null or a fully-qualified local file path.",
                parameterName);
        }
        _ = Path.GetFullPath(path);
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
        _session.ConfigureAudioCache(
            _current.AudioCache.RootPath,
            _current.AudioCache.MaximumReusableBytes);
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
    public event EventHandler? AudioCachePreferencesChanged;

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
            audioCacheMayChange: false,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateAudioCache(
        AudioCachePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        AudioCachePreferences normalized;
        try
        {
            normalized = preferences.Normalize();
            normalized.Validate();
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
            current => current with { AudioCache = normalized },
            realtimeMayChange: false,
            audioCacheMayChange: true,
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
            audioCacheMayChange: false,
            requiresPlaybackStopped: false);
    }

    public ApplicationPreferenceUpdateResult ResetToDefaults()
    {
        return Persist(
            _ => ApplicationPreferences.Default,
            realtimeMayChange: true,
            audioCacheMayChange: true,
            requiresPlaybackStopped: true);
    }

    private ApplicationPreferenceUpdateResult Persist(
        Func<ApplicationPreferences, ApplicationPreferences> update,
        bool realtimeMayChange,
        bool audioCacheMayChange,
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
        bool audioCacheChanged;
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
                    audioCacheChanged = audioCacheMayChange
                        && !Equals(_current.AudioCache, candidate.AudioCache);
                    _current = candidate;
                }
                else
                {
                    realtimeChanged = !Equals(
                        _current.RealtimeAudio,
                        ApplicationPreferences.Default.RealtimeAudio);
                    audioCacheChanged = !Equals(
                        _current.AudioCache,
                        ApplicationPreferences.Default.AudioCache);
                    _current = ApplicationPreferences.Default;
                }
            }

            if (realtimeChanged)
            {
                if (!audioCacheChanged)
                {
                    _ = _session.ResetAudioCacheGenerations();
                }
                RealtimeAudioPreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            if (audioCacheChanged)
            {
                AudioCachePreferences cache = Current.AudioCache;
                _session.ConfigureAudioCache(cache.RootPath, cache.MaximumReusableBytes);
                AudioCachePreferencesChanged?.Invoke(this, EventArgs.Empty);
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
