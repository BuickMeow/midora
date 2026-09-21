using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Midora.Application;

namespace Midora.Avalonia;

public partial class App : global::Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ApplicationPreferences preferences = ApplicationPreferences.Default;
            string? preferencesNotice = null;
            try
            {
                ApplicationPreferencesLoadResult loaded = new ApplicationPreferencesStore().Load();
                preferences = loaded.Preferences;
                preferencesNotice = loaded.Notice?.Message;
            }
            catch (Exception exception)
            {
                preferencesNotice = "Application Preferences could not be read: " + exception.Message;
            }

            var mainWindow = new MainWindow(preferences);
            desktop.MainWindow = mainWindow;
            if (preferencesNotice is not null)
            {
                string notice = preferencesNotice;
                mainWindow.Opened += (_, _) => mainWindow.Session.SetStatus(notice);
            }

            if (desktop.Args?.Contains("--smoke-windows", StringComparer.Ordinal) == true)
            {
                mainWindow.Opened += async (_, _) =>
                {
                    var exitCode = await mainWindow.RunWindowSmokeAsync();
                    desktop.Shutdown(exitCode);
                };
            }
            else if (desktop.Args?.Contains("--smoke-shell", StringComparer.Ordinal) == true)
            {
                mainWindow.Opened += async (_, _) =>
                {
                    var exitCode = await mainWindow.RunShellSmokeAsync();
                    desktop.Shutdown(exitCode);
                };
            }
            else
            {
                var reviewMidi = Environment.GetEnvironmentVariable("MIDORA_MIDI_OPEN");
                if (!string.IsNullOrEmpty(reviewMidi) && File.Exists(reviewMidi))
                {
                    mainWindow.Opened += (_, _) => mainWindow.OpenMidiForReview(reviewMidi);
                }

                if (int.TryParse(Environment.GetEnvironmentVariable("MIDORA_OPEN_TRACK"), out int trackIndex))
                {
                    mainWindow.Opened += (_, _) => mainWindow.OpenTrackForReview(trackIndex);
                }

                var segment = Environment.GetEnvironmentVariable("MIDORA_OPEN_SEGMENT");
                if (!string.IsNullOrEmpty(segment))
                {
                    string[] parts = segment.Split(':');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int segmentTrack) &&
                        long.TryParse(parts[1], out long segmentTick))
                    {
                        mainWindow.Opened += (_, _) =>
                            mainWindow.OpenSegmentForReview(segmentTrack, segmentTick);
                    }
                }

                if (Environment.GetEnvironmentVariable("MIDORA_AUTOPLAY") == "1")
                {
                    mainWindow.Opened += (_, _) => mainWindow.StartPlaybackForReview();
                }

                var trackMode = Environment.GetEnvironmentVariable("MIDORA_TRACK_MODE");
                if (!string.IsNullOrEmpty(trackMode))
                {
                    mainWindow.Opened += (_, _) => mainWindow.SetTrackModeForReview(trackMode);
                }

                if (Environment.GetEnvironmentVariable("MIDORA_NEW_PROJECT") == "1")
                {
                    mainWindow.Opened += (_, _) => mainWindow.NewProjectForReview();
                }

                if (Environment.GetEnvironmentVariable("MIDORA_DIAGNOSTICS") == "1")
                {
                    mainWindow.Opened += async (_, _) =>
                        await mainWindow.ShowDiagnosticsForReviewAsync();
                }

                if (Environment.GetEnvironmentVariable("MIDORA_MAXIMIZE") == "1")
                {
                    mainWindow.Opened += (_, _) =>
                        mainWindow.WindowState = WindowState.Maximized;
                }

                if (Environment.GetEnvironmentVariable("MIDORA_WINDOW_CYCLE") == "1")
                {
                    mainWindow.Opened += async (_, _) =>
                    {
                        await Task.Delay(5000);
                        mainWindow.WindowState = WindowState.Maximized;
                        await Task.Delay(15000);
                        mainWindow.WindowState = WindowState.Normal;
                        await Task.Delay(10000);
                        mainWindow.Width = 1200;
                        mainWindow.Height = 700;
                    };
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
