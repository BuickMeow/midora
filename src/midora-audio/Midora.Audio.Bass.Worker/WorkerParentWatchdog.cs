using System.Runtime.InteropServices;

namespace Midora.Audio.Bass.Worker;

/// <summary>
/// macOS/Linux parent-death detection. Windows workers are terminated by the owning
/// process's Job Object kill-on-close; the other platforms watch the parent process id and
/// treat reparenting (ppid becomes 1) as owner death so a crashed application cannot leave
/// the audio worker playing forever.
/// </summary>
internal static partial class WorkerParentWatchdog
{
    private static readonly int InitialParentProcessId =
        OperatingSystem.IsWindows() ? 0 : GetParentProcessId();
    private static volatile bool _parentExited;

    public static bool ParentExited => _parentExited;

    public static void Start()
    {
        if (OperatingSystem.IsWindows() || InitialParentProcessId <= 1)
        {
            return;
        }

        Thread thread = new(Watch)
        {
            IsBackground = true,
            Name = "midora-parent-watchdog"
        };
        thread.Start();
    }

    private static void Watch()
    {
        while (!_parentExited)
        {
            Thread.Sleep(500);
            if (GetParentProcessId() != InitialParentProcessId)
            {
                _parentExited = true;
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParentProcessId();
}
