using System.Runtime.InteropServices;

namespace Midora.Common;

/// <summary>
/// Defines the stable Windows shell identity shared by every Midora process.
/// </summary>
public static class MidoraWindowsApplicationIdentity
{
    public const string AppUserModelId = "Zacksony.Midora";

    public static bool TryApplyToCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return SetCurrentProcessExplicitAppUserModelID(AppUserModelId) >= 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
