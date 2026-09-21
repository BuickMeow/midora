using Avalonia;
using Midora.Common;

namespace Midora.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // The portable data root must be validated before the primary window exists; Midora fails
        // closed instead of falling back to another location.
        try
        {
            MidoraProgramData.EnsureReadyAndProbe();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "Midora cannot start because its portable data root is unusable: "
                + exception.Message);
            Environment.ExitCode = 1;
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
