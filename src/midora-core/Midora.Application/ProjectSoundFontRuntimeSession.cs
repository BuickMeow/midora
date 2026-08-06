using Midora.Audio;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application;

public enum ProjectSoundFontAvailability
{
    NotVerified,
    Verifying,
    VerificationRequired,
    NoReference,
    ProjectPathRequired,
    Missing,
    Ambiguous,
    Unreadable,
    UnsupportedOrCorrupt,
    BackendUnavailable,
    EmbeddedResourceUnavailable,
    Available
}

public sealed record ProjectSoundFontRuntimeSnapshot(
    ProjectSoundFontAvailability Availability,
    ProjectSoundFontReference? Reference,
    string? ResolvedAbsolutePath = null,
    ExternalSoundFontResolutionKind? ExternalResolution = null,
    bool? HashMatches = null,
    bool UsedCaseInsensitiveFallback = false,
    SoundFontLoadabilityFailure? LoadabilityFailure = null)
{
    public bool IsAvailable => Availability == ProjectSoundFontAvailability.Available;
    public bool RequiresWarning => IsAvailable
        && (HashMatches == false || UsedCaseInsensitiveFallback);
}

public sealed class ProjectSoundFontRuntimeSession : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ProjectCompilationSession _compilation;
    private readonly ISoundFontLoadabilityValidator _loadabilityValidator;
    private readonly ExternalSoundFontVerificationCacheV1 _externalCache = new();
    private ProjectSoundFontRuntimeSnapshot _current = new(
        ProjectSoundFontAvailability.NotVerified,
        Reference: null);
    private ExternalProjectSoundFontReference? _watchedExternalReference;
    private bool _disposeStarted;
    private bool _disposed;

    public ProjectSoundFontRuntimeSession(
        ProjectCompilationSession compilation,
        ISoundFontLoadabilityValidator loadabilityValidator)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _loadabilityValidator = loadabilityValidator
            ?? throw new ArgumentNullException(nameof(loadabilityValidator));
        _externalCache.Invalidated += OnExternalCacheInvalidated;
        _compilation.CompilationChanged += OnCompilationChanged;
    }

    public ProjectSoundFontRuntimeSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _current;
            }
        }
    }

    public event EventHandler? AvailabilityChanged;

    public bool TryConfirmReadyForAudioStart()
    {
        ProjectSoundFontRuntimeSnapshot snapshot = Current;
        if (!snapshot.IsAvailable || snapshot.Reference is null)
        {
            return false;
        }
        if (snapshot.Reference is ExternalProjectSoundFontReference external)
        {
            return _externalCache.TryConfirmCurrent(external);
        }
        if (string.IsNullOrWhiteSpace(snapshot.ResolvedAbsolutePath)
            || !File.Exists(snapshot.ResolvedAbsolutePath))
        {
            _compilation.TryInvalidateSoundFontResource(snapshot.Reference);
            Publish(new(
                ProjectSoundFontAvailability.VerificationRequired,
                snapshot.Reference));
            return false;
        }
        return true;
    }

    public async Task<ProjectSoundFontRuntimeSnapshot> RefreshAsync(
        string? currentProjectFilePath,
        EmbeddedSoundFontResourceV1? embeddedResource = null,
        bool forceFullExternalVerification = false,
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ProjectSoundFontReference? reference = _compilation.Project.SoundFont.Reference;
            if (!_compilation.TryBeginSoundFontVerification(reference))
            {
                return Publish(new(
                    ProjectSoundFontAvailability.VerificationRequired,
                    _compilation.Project.SoundFont.Reference));
            }
            Publish(new(ProjectSoundFontAvailability.Verifying, reference));
            try
            {
                if (reference is null)
                {
                    SetWatchedExternalReference(reference: null);
                    return Publish(new(ProjectSoundFontAvailability.NoReference, null));
                }
                if (reference is ExternalProjectSoundFontReference external)
                {
                    return await RefreshExternalAsync(
                        currentProjectFilePath,
                        external,
                        forceFullExternalVerification,
                        cancellationToken).ConfigureAwait(false);
                }
                SetWatchedExternalReference(reference: null);
                return await RefreshEmbeddedAsync(
                    (EmbeddedProjectSoundFontReference)reference,
                    embeddedResource,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Publish(new(
                    ProjectSoundFontAvailability.VerificationRequired,
                    reference));
                throw;
            }
            catch (IOException)
            {
                return Publish(new(ProjectSoundFontAvailability.Unreadable, reference));
            }
            catch (UnauthorizedAccessException)
            {
                return Publish(new(ProjectSoundFontAvailability.Unreadable, reference));
            }
            catch (MidoraAudioException)
            {
                return Publish(new(ProjectSoundFontAvailability.BackendUnavailable, reference));
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposeStarted)
            {
                return;
            }
            _disposeStarted = true;
            _disposed = true;
        }
        _externalCache.Invalidated -= OnExternalCacheInvalidated;
        _compilation.CompilationChanged -= OnCompilationChanged;
        _externalCache.Dispose();
    }

    private async Task<ProjectSoundFontRuntimeSnapshot> RefreshExternalAsync(
        string? currentProjectFilePath,
        ExternalProjectSoundFontReference reference,
        bool forceFullVerification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentProjectFilePath)
            || !Path.IsPathFullyQualified(currentProjectFilePath))
        {
            return Publish(new(
                ProjectSoundFontAvailability.ProjectPathRequired,
                reference));
        }

        ExternalSoundFontVerificationV1 verified = await _externalCache.VerifyAsync(
            currentProjectFilePath,
            reference,
            forceFullVerification,
            cancellationToken).ConfigureAwait(false);
        if (verified.IsReadable)
        {
            SetWatchedExternalReference(reference);
        }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ProjectSoundFontRuntimeSnapshot? unavailable = MapUnavailable(reference, verified);
            if (unavailable is not null)
            {
                return Publish(unavailable);
            }

            try
            {
                await _loadabilityValidator.ValidateAsync(
                    verified.ResolvedAbsolutePath!,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SoundFontLoadabilityException exception)
            {
                return Publish(MapLoadabilityFailure(reference, verified, exception.Failure));
            }

            ExternalSoundFontVerificationV1 afterLoad = await _externalCache.VerifyAsync(
                currentProjectFilePath,
                reference,
                forceFullVerification: false,
                cancellationToken).ConfigureAwait(false);
            if (verified.FileStamp == afterLoad.FileStamp
                && afterLoad.IsReadable
                && !_externalCache.IsInvalidated)
            {
                if (!_compilation.TrySetVerifiedSoundFontPath(
                    reference,
                    afterLoad.ResolvedAbsolutePath!))
                {
                    return Publish(new(
                        ProjectSoundFontAvailability.VerificationRequired,
                        _compilation.Project.SoundFont.Reference));
                }
                return Publish(new(
                    ProjectSoundFontAvailability.Available,
                    reference,
                    afterLoad.ResolvedAbsolutePath,
                    afterLoad.Resolution,
                    afterLoad.HashMatches,
                    afterLoad.Resolution == ExternalSoundFontResolutionKind.CaseInsensitiveFallback));
            }
            verified = afterLoad;
        }
        return Publish(new(ProjectSoundFontAvailability.VerificationRequired, reference));
    }

    private async Task<ProjectSoundFontRuntimeSnapshot> RefreshEmbeddedAsync(
        EmbeddedProjectSoundFontReference reference,
        EmbeddedSoundFontResourceV1? resource,
        CancellationToken cancellationToken)
    {
        if (resource is null
            || !resource.IsAvailable
            || resource.Reference != reference
            || string.IsNullOrWhiteSpace(resource.ResolvedAbsolutePath)
            || !File.Exists(resource.ResolvedAbsolutePath))
        {
            return Publish(new(
                ProjectSoundFontAvailability.EmbeddedResourceUnavailable,
                reference));
        }
        try
        {
            await _loadabilityValidator.ValidateAsync(
                resource.ResolvedAbsolutePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SoundFontLoadabilityException exception)
        {
            return Publish(MapLoadabilityFailure(
                reference,
                verification: null,
                exception.Failure));
        }
        if (!_compilation.TrySetVerifiedSoundFontPath(
            reference,
            resource.ResolvedAbsolutePath))
        {
            return Publish(new(
                ProjectSoundFontAvailability.VerificationRequired,
                _compilation.Project.SoundFont.Reference));
        }
        return Publish(new(
            ProjectSoundFontAvailability.Available,
            reference,
            resource.ResolvedAbsolutePath));
    }

    private static ProjectSoundFontRuntimeSnapshot? MapUnavailable(
        ExternalProjectSoundFontReference reference,
        ExternalSoundFontVerificationV1 verification) =>
        verification.Resolution switch
        {
            ExternalSoundFontResolutionKind.Missing => new(
                ProjectSoundFontAvailability.Missing,
                reference,
                ExternalResolution: verification.Resolution),
            ExternalSoundFontResolutionKind.Ambiguous => new(
                ProjectSoundFontAvailability.Ambiguous,
                reference,
                ExternalResolution: verification.Resolution),
            ExternalSoundFontResolutionKind.Unreadable => new(
                ProjectSoundFontAvailability.Unreadable,
                reference,
                ExternalResolution: verification.Resolution),
            _ => null
        };

    private static ProjectSoundFontRuntimeSnapshot MapLoadabilityFailure(
        ProjectSoundFontReference reference,
        ExternalSoundFontVerificationV1? verification,
        SoundFontLoadabilityFailure failure) =>
        new(
            failure switch
            {
                SoundFontLoadabilityFailure.Missing =>
                    ProjectSoundFontAvailability.Missing,
                SoundFontLoadabilityFailure.Unreadable =>
                    ProjectSoundFontAvailability.Unreadable,
                SoundFontLoadabilityFailure.UnsupportedOrCorrupt =>
                    ProjectSoundFontAvailability.UnsupportedOrCorrupt,
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            },
            reference,
            verification?.ResolvedAbsolutePath,
            verification?.Resolution,
            verification?.HashMatches,
            verification?.Resolution == ExternalSoundFontResolutionKind.CaseInsensitiveFallback,
            failure);

    private void OnExternalCacheInvalidated(object? sender, EventArgs e)
    {
        ExternalProjectSoundFontReference? reference;
        lock (_sync)
        {
            if (_disposeStarted)
            {
                return;
            }
            reference = _watchedExternalReference;
        }
        if (reference is null)
        {
            return;
        }
        try
        {
            if (!_compilation.TryInvalidateSoundFontResource(reference))
            {
                return;
            }
            Publish(new(
                ProjectSoundFontAvailability.VerificationRequired,
                reference));
        }
        catch (ObjectDisposedException)
        {
            // Project shutdown can race a queued FileSystemWatcher notification.
        }
    }

    private void OnCompilationChanged(object? sender, EventArgs e)
    {
        ProjectSoundFontReference? sourceReference =
            _compilation.Project.SoundFont.Reference;
        lock (_sync)
        {
            if (_disposeStarted || _current.Reference == sourceReference)
            {
                return;
            }
        }
        try
        {
            if (sourceReference is null)
            {
                if (!_compilation.TryBeginSoundFontVerification(expectedReference: null))
                {
                    return;
                }
                SetWatchedExternalReference(reference: null);
                Publish(new(ProjectSoundFontAvailability.NoReference, null));
                return;
            }
            if (!_compilation.TryInvalidateSoundFontResource(sourceReference))
            {
                return;
            }
            Publish(new(
                ProjectSoundFontAvailability.VerificationRequired,
                sourceReference));
        }
        catch (ObjectDisposedException)
        {
            // Project shutdown can race a queued compilation notification.
        }
    }

    private void SetWatchedExternalReference(
        ExternalProjectSoundFontReference? reference)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watchedExternalReference = reference;
        }
    }

    private ProjectSoundFontRuntimeSnapshot Publish(
        ProjectSoundFontRuntimeSnapshot value)
    {
        bool changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            changed = _current != value;
            _current = value;
        }
        if (changed)
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
        return value;
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
        }
    }
}
