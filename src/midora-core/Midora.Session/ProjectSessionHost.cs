using Midora.Application;
using Midora.Common;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Midora.Session.Audio;

namespace Midora.Session;

/// <summary>
/// Host composition options. Realtime playback stays opt-in so Project-only consumers (import,
/// MIDI export, persistence) never load the audio worker.
/// </summary>
public sealed record ProjectSessionHostOptions(
    bool EnableRealtimePlayback = false,
    ApplicationPreferences? Preferences = null);

/// <summary>Summary of the Project that is currently open, for shell status and titles.</summary>
public sealed record ProjectActivation(
    string ProjectName,
    string? CurrentPath,
    bool IsPersisted,
    int LogicalTrackCount,
    int PureMidiTrackCount,
    bool HasDamagedObjects,
    bool RequiresFormatUpgrade,
    string? SourcePath);

/// <summary>
/// Application-side adapter over the platform-neutral Application / Persistence / Compiler layers.
/// Mirrors the Project lifecycle, persistence, compilation and realtime playback responsibilities
/// of the WPF <c>DesktopSessionController.ProjectContext</c>.
/// </summary>
public sealed class ProjectSessionHost : IDisposable
{
    private readonly MidoraProjectPackageV1 _packages;
    private readonly ProjectCreationCoordinator _creation;
    private readonly ProjectOpenCoordinator _opening;
    private ProjectSessionHostOptions _options;
    private ProjectContext? _context;

    public ProjectSessionHost(string softwareVersion, ProjectSessionHostOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareVersion);
        _options = options ?? new ProjectSessionHostOptions();
        Paths = MidoraProgramData.Resolve(AppContext.BaseDirectory);
        MidoraProgramData.EnsureReadyAndProbe(Paths);
        _packages = new MidoraProjectPackageV1(softwareVersion);
        _creation = new ProjectCreationCoordinator(_packages);
        _opening = new ProjectOpenCoordinator(_packages);
    }

    public MidoraProgramDataPaths Paths { get; }

    public bool HasProject => _context is not null;

    public ProjectDocumentSession? Document => _context?.Document;

    public ProjectPersistenceCoordinator? Persistence => _context?.Persistence;

    public ProjectCompilationSession? Compilation => _context?.Compilation;

    /// <summary>Realtime playback for the open Project, or null when playback is disabled or failed.</summary>
    public RealtimePlaybackSession? Playback => _context?.Playback;

    /// <summary>Why realtime playback is unavailable, when it could not be created.</summary>
    public string? PlaybackFailure => _context?.PlaybackFailure;

    public ProjectActivation CreateUnsaved(NewProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The CreateUnsaved path performs no asynchronous work; blocking keeps the shell's
        // synchronous review/smoke hooks (MIDORA_NEW_PROJECT) unchanged.
        NewProjectCreationResult created = _creation.CreateAsync(request).GetAwaiter().GetResult();
        return Adopt(ProjectContext.FromCreation(_packages, created, _options), created);
    }

    public async Task<ProjectActivation> CreateAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NewProjectCreationResult created = await _creation
            .CreateAsync(request, cancellationToken).ConfigureAwait(true);
        return Adopt(ProjectContext.FromCreation(_packages, created, _options), created);
    }

    public async Task<ProjectActivation> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ProjectOpenCandidate candidate = await _opening
            .OpenAsync(path, progress: null, cancellationToken).ConfigureAwait(true);
        ProjectContext context;
        try
        {
            context = ProjectContext.FromOpenCandidate(candidate, _options);
        }
        catch
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return Adopt(context, candidate);
    }

    public ProjectActivation ImportMidi(byte[] file, string projectName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        MidiProjectImportResult imported = MidiProjectImportService.Import(
            file,
            projectName,
            zeroBasedPortMapping: null,
            cancellationToken);
        NewProjectCreationResult adopted;
        try
        {
            adopted = _creation.AdoptImportedProject(imported.Project);
        }
        catch
        {
            imported.Project.Dispose();
            throw;
        }

        return Adopt(ProjectContext.FromCreation(_packages, adopted, _options), adopted);
    }

    /// <summary>
    /// Applies persisted preferences. Audio-relevant changes destroy the current playback session
    /// (and with it the audio worker) and prewarm a replacement, as required for the
    /// <c>Saving Settings</c> runtime stage.
    /// </summary>
    public async Task ApplyPreferencesAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ProjectContext? context = _context;
        bool audioChanged = context is not null
            && !AudioPreferencesEqual(context.Preferences, preferences);
        _options = _options with { Preferences = preferences };
        if (context is null)
        {
            return;
        }

        context.ApplyPreferences(preferences, _options);
        if (!audioChanged || context.Playback is null)
        {
            return;
        }

        await context.Playback.WarmUpAsync(cancellationToken).ConfigureAwait(true);
    }

    private static bool AudioPreferencesEqual(
        ApplicationPreferences? left,
        ApplicationPreferences right)
    {
        if (left is null)
        {
            return false;
        }

        return left.RealtimeAudio == right.RealtimeAudio
            && left.AudioCache == right.AudioCache
            && left.Playback == right.Playback
            && SoundFontsEqual(left.SoundFonts, right.SoundFonts);
    }

    private static bool SoundFontsEqual(
        IReadOnlyList<ApplicationSoundFontPreference> left,
        IReadOnlyList<ApplicationSoundFontPreference> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].EntryId != right[index].EntryId
                || !string.Equals(left[index].Path, right[index].Path, StringComparison.Ordinal)
                || left[index].Enabled != right[index].Enabled
                || left[index].Target != right[index].Target)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<MidoraProjectSaveResultV1> SaveAsync(
        string? firstSaveTargetPath = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        ProjectPersistenceCoordinator persistence = RequireContext().Persistence;
        return await persistence
            .SaveProjectAsync(firstSaveTargetPath, overwriteAuthorized, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<MidoraProjectSaveResultV1> SaveCopyAsync(
        string targetPath,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        ProjectPersistenceCoordinator persistence = RequireContext().Persistence;
        return await persistence
            .SaveCopyAsync(targetPath, overwriteAuthorized, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Runs a full synchronous compile of the current Project on the caller's thread.</summary>
    public CanonicalCompiledResult Compile() =>
        RequireContext().Compilation.Recompile(ProjectChangeSet.Everything);

    public void Close()
    {
        ProjectContext? previous = Interlocked.Exchange(ref _context, null);
        previous?.Dispose();
    }

    public void Dispose() => Close();

    private ProjectActivation Adopt(ProjectContext context, IAsyncDisposable owner)
    {
        ProjectContext? previous = Interlocked.Exchange(ref _context, context);
        previous?.Dispose();
        return Describe(context, owner);
    }

    private static ProjectActivation Describe(ProjectContext context, IAsyncDisposable owner)
    {
        MidoraProject project = context.Document.Project;
        string? sourcePath = owner switch
        {
            ProjectOpenCandidate candidate => candidate.SourceProjectPath,
            _ => null
        };
        bool requiresUpgrade = owner is ProjectOpenCandidate { CurrentProjectPath: null };
        bool hasDamaged = project.DamagedEventInstruments.Count != 0
            || project.DamagedLogicalTracks.Count != 0
            || project.DamagedMidiChannelRoots.Count != 0
            || project.DamagedPureMidiTracks.Count != 0;
        return new ProjectActivation(
            project.Metadata.ProjectName,
            context.Persistence.CurrentProjectPath,
            context.Document.HasPersistentOrigin,
            project.Tracks.Count,
            project.PureMidiTracks.Count,
            hasDamaged,
            requiresUpgrade,
            sourcePath);
    }

    private ProjectContext RequireContext() =>
        _context ?? throw new InvalidOperationException("No Project is open.");

    private sealed class ProjectContext : IDisposable
    {
        private readonly IAsyncDisposable _owner;
        private int _disposeStarted;

        private ProjectContext(
            IAsyncDisposable owner,
            ProjectCompilationSession compilation,
            ProjectDocumentSession document,
            ProjectPersistenceCoordinator persistence,
            RealtimePlaybackSession? playback,
            string? playbackFailure,
            ApplicationPreferences? preferences)
        {
            _owner = owner;
            Compilation = compilation;
            Document = document;
            Persistence = persistence;
            Playback = playback;
            PlaybackFailure = playbackFailure;
            Preferences = preferences;
        }

        public ProjectCompilationSession Compilation { get; }

        public ProjectDocumentSession Document { get; }

        public ProjectPersistenceCoordinator Persistence { get; }

        public RealtimePlaybackSession? Playback { get; private set; }

        public string? PlaybackFailure { get; private set; }

        public ApplicationPreferences? Preferences { get; private set; }

        public static ProjectContext FromCreation(
            MidoraProjectPackageV1 packages,
            NewProjectCreationResult result,
            ProjectSessionHostOptions options)
        {
            ProjectCompilationSession compilation = CreateCompilation(result.Project, options);
            try
            {
                ProjectDocumentSession document = new(compilation, result.Origin);
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    result.CurrentProjectPath,
                    result.FileInformation);
                CreatePlayback(compilation, options, out RealtimePlaybackSession? playback, out string? failure);
                return new(result, compilation, document, persistence, playback, failure, options.Preferences);
            }
            catch
            {
                compilation.Dispose();
                throw;
            }
        }

        public static ProjectContext FromOpenCandidate(
            ProjectOpenCandidate candidate,
            ProjectSessionHostOptions options)
        {
            ProjectCompilationSession compilation = CreateCompilation(candidate.Project, options);
            try
            {
                ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
                ProjectPersistenceCoordinator persistence =
                    candidate.CreatePersistenceCoordinator(document);
                CreatePlayback(compilation, options, out RealtimePlaybackSession? playback, out string? failure);
                return new(candidate, compilation, document, persistence, playback, failure, options.Preferences);
            }
            catch
            {
                compilation.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Re-applies preferences. When the audio-relevant settings changed, the existing playback
        /// session (and its worker) is destroyed and replaced with a freshly configured one.
        /// </summary>
        public void ApplyPreferences(ApplicationPreferences preferences, ProjectSessionHostOptions options)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
            Preferences = preferences;
            if (!options.EnableRealtimePlayback)
            {
                return;
            }

            Playback?.Dispose();
            Playback = null;
            CreatePlayback(Compilation, options, out RealtimePlaybackSession? playback, out string? failure);
            Playback = playback;
            PlaybackFailure = failure;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            Playback?.Dispose();
            Document.Dispose();
            Compilation.Dispose();
            _owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static ProjectCompilationSession CreateCompilation(
            MidoraProject project,
            ProjectSessionHostOptions options) =>
            new(
                project,
                executionMode: options.EnableRealtimePlayback
                    ? ProjectCompilationExecutionMode.Background
                    : ProjectCompilationExecutionMode.Synchronous);

        private static void CreatePlayback(
            ProjectCompilationSession compilation,
            ProjectSessionHostOptions options,
            out RealtimePlaybackSession? playback,
            out string? failure)
        {
            playback = null;
            failure = null;
            if (!options.EnableRealtimePlayback || options.Preferences is not { } preferences)
            {
                return;
            }

            compilation.SetEffectiveSoundFontConfigurations(
                preferences.GetEnabledSoundFontConfigurations());
            if (!RealtimePlaybackSession.TryCreate(compilation, preferences, out playback, out failure))
            {
                playback = null;
            }
        }
    }
}
