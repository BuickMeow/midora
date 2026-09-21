using System.Runtime.InteropServices;

namespace Midora.Common;

/// <summary>
/// Stable application identity shared by every Midora process. Windows requires the shell identity
/// to be applied explicitly so taskbar grouping and notifications use one AppUserModelID; the other
/// platforms have no equivalent registration, so the call is a documented no-op there.
/// </summary>
public static class MidoraApplicationIdentity
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
