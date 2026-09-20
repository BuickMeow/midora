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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
