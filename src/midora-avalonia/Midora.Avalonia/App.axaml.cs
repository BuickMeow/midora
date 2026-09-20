using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Midora.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

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
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
