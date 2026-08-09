using System.IO;
using System.Runtime.CompilerServices;

namespace Midora.Desktop;

internal static class WindowsLaunchEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        string? windowsDirectory = Environment.GetEnvironmentVariable("windir");
        if (!string.IsNullOrWhiteSpace(windowsDirectory)
            && Path.IsPathFullyQualified(windowsDirectory))
        {
            return;
        }

        string? systemDirectory = Environment.SystemDirectory;
        string? recoveredWindowsDirectory = Path.GetDirectoryName(systemDirectory);
        if (string.IsNullOrWhiteSpace(recoveredWindowsDirectory)
            || !Path.IsPathFullyQualified(recoveredWindowsDirectory))
        {
            return;
        }

        Environment.SetEnvironmentVariable("windir", recoveredWindowsDirectory);
    }
}
