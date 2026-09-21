using Midora.Application;
using Midora.Common;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Session;

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
/// Avalonia-side adapter over the platform-neutral Application / Persistence / Compiler layers.
/// Mirrors the Project lifecycle, persistence and compilation responsibilities of the WPF
/// <c>DesktopSessionController.ProjectContext</c>; audio and playback services are deliberately
/// absent because they depend on the not-yet-decided macOS audio backend.
/// </summary>
public sealed class ProjectSessionHost : IDisposable
{
    private readonly MidoraProjectPackageV1 _packages;
    private readonly ProjectCreationCoordinator _creation;
    private readonly ProjectOpenCoordinator _opening;
    private ProjectContext? _context;

    public ProjectSessionHost(string softwareVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(softwareVersion);
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

    public ProjectActivation CreateUnsaved(NewProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The CreateUnsaved path performs no asynchronous work; blocking keeps the shell's
        // synchronous review/smoke hooks (MIDORA_NEW_PROJECT) unchanged.
        NewProjectCreationResult created = _creation.CreateAsync(request).GetAwaiter().GetResult();
        return Adopt(ProjectContext.FromCreation(_packages, created), created);
    }

    public async Task<ProjectActivation> CreateAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NewProjectCreationResult created = await _creation
            .CreateAsync(request, cancellationToken).ConfigureAwait(true);
        return Adopt(ProjectContext.FromCreation(_packages, created), created);
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
            context = ProjectContext.FromOpenCandidate(candidate);
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

        return Adopt(ProjectContext.FromCreation(_packages, adopted), adopted);
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
            ProjectPersistenceCoordinator persistence)
        {
            _owner = owner;
            Compilation = compilation;
            Document = document;
            Persistence = persistence;
        }

        public ProjectCompilationSession Compilation { get; }

        public ProjectDocumentSession Document { get; }

        public ProjectPersistenceCoordinator Persistence { get; }

        public static ProjectContext FromCreation(
            MidoraProjectPackageV1 packages,
            NewProjectCreationResult result)
        {
            ProjectCompilationSession compilation = new(
                result.Project,
                executionMode: ProjectCompilationExecutionMode.Synchronous);
            try
            {
                ProjectDocumentSession document = new(compilation, result.Origin);
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    result.CurrentProjectPath,
                    result.FileInformation);
                return new(result, compilation, document, persistence);
            }
            catch
            {
                compilation.Dispose();
                throw;
            }
        }

        public static ProjectContext FromOpenCandidate(ProjectOpenCandidate candidate)
        {
            ProjectCompilationSession compilation = new(
                candidate.Project,
                executionMode: ProjectCompilationExecutionMode.Synchronous);
            try
            {
                ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
                ProjectPersistenceCoordinator persistence =
                    candidate.CreatePersistenceCoordinator(document);
                return new(candidate, compilation, document, persistence);
            }
            catch
            {
                compilation.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            Document.Dispose();
            Compilation.Dispose();
            _owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
