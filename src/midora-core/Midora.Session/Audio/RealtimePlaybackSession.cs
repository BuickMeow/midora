using Midora.Application;
using Midora.Audio;
using Midora.Audio.Bass;
using Midora.Playback;
using Midora.Playback.BassWasapi;

namespace Midora.Session.Audio;

/// <summary>
/// Formal realtime playback for the application shell: it owns the child-process backend and the
/// <see cref="PlaybackController"/> that compiles the Project, projects the canonical result into a
/// realtime render plan at the output device's actual sample rate, and drives the worker.
/// </summary>
public sealed class RealtimePlaybackSession : IDisposable
{
    private static readonly TimeSpan PreparingTimeout = TimeSpan.FromSeconds(30);

    private readonly BassWasapiChildPlaybackBackend _backend;
    private readonly PlaybackController _playback;
    private bool _disposed;

    private RealtimePlaybackSession(
        BassWasapiChildPlaybackBackend backend,
        PlaybackController playback)
    {
        _backend = backend;
        _playback = playback;
    }

    public PlaybackState State => _playback.State;

    public long CurrentTick => _playback.CurrentTick;

    public bool IsActive =>
        _playback.State is PlaybackState.Preparing
            or PlaybackState.Playing
            or PlaybackState.Buffering
            or PlaybackState.Stopping;

    /// <summary>True when the output device disappeared and the user must choose one.</summary>
    public bool OutputDeviceSelectionRequired => _playback.OutputDeviceSelectionRequired;

    public string? FailureMessage { get; private set; }

    public static bool TryCreate(
        ProjectCompilationSession compilation,
        ApplicationPreferences preferences,
        out RealtimePlaybackSession? session,
        out string? failure)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(preferences);
        session = null;
        if (!FormalAudioWorkerLocator.TryLocate(
                out string? workerPath,
                out string? nativeDirectory,
                out failure))
        {
            return false;
        }

        BassWasapiChildPlaybackBackend? backend = null;
        PlaybackController? playback = null;
        try
        {
            RealtimeAudioPreferences audio = preferences.RealtimeAudio;
            backend = new(new(
                workerPath!,
                nativeDirectory!,
                audio.PlaybackOutputDeviceId,
                audio.RenderAheadMilliseconds,
                audio.DeviceBufferRequestMilliseconds,
                new BassMidiRendererSettings(
                    audio.MaximumSampleVoicesPerUnitStream,
                    InitialReleaseAudioRuntimePolicy.WorkFrameCount),
                new AudioMasterSettings(
                    checked((float)preferences.Playback.MasterVolumeDecibels),
                    AudioMasterSettings.LimiterCeilingV2,
                    AudioMasterSettings.LimiterReleaseMillisecondsV2),
                PreparingTimeout));
            compilation.ConfigureAudioCache(
                preferences.AudioCache.RootPath,
                preferences.AudioCache.MaximumReusableBytes);
            playback = new(
                compilation,
                backend,
                new PlaybackMasterConfiguration(
                    checked((float)preferences.Playback.MasterVolumeDecibels),
                    preferences.Playback.LimiterEnabled),
                preferences.Playback.StopCursorBehavior);
            session = new(backend, playback);
            // Kick the default-plan prewarm so the first Play only pays for the range compile.
            playback.BeginDefaultPlaybackPreparation();
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            playback?.Dispose();
            backend?.Dispose();
            failure = exception.Message;
            return false;
        }
    }

    /// <summary>Prepares the worker and SoundFont host so the first Play does not pay for them.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => _playback.WarmUpAudioBackend(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            if (Environment.GetEnvironmentVariable("MIDORA_PLAYBACK_TRACE") == "1")
            {
                Console.Out.WriteLine($"MIDORA-PLAYBACK warm-up finished in {stopwatch.ElapsedMilliseconds} ms");
                Console.Out.Flush();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FailureMessage = "Audio warm-up failed: " + exception.Message;
        }
    }

    public void Start(long? cursorTick = null, long? endTick = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FailureMessage = null;
        _playback.Start(cursorTick, endTick);
    }

    /// <summary>Pumps playback state; the shell calls this from its UI timer.</summary>
    public void Update()
    {
        if (_disposed)
        {
            return;
        }

        _playback.Update();
        if (_playback.State == PlaybackState.Error && FailureMessage is null)
        {
            FailureMessage = "Realtime playback stopped because the audio backend reported an error.";
        }
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        _playback.Stop();
    }

    public void Seek(long tick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _playback.Seek(tick);
    }

    public void SelectOutputDevice(string? deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _playback.SelectOutputDevice(deviceId);
    }

    public void ResetPlaybackEngine()
    {
        if (_disposed)
        {
            return;
        }

        _playback.ResetPlaybackEngine();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playback.Dispose();
        _backend.Dispose();
    }
}
