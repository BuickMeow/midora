using global::Avalonia.Threading;

namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Avalonia 11.3 does not expose <c>Dispatcher.HasShutdownStarted</c> publicly. This shim
/// tracks the public <c>ShutdownStarted</c> event so ported WPF code keeps its guard.
/// </summary>
internal static class DispatcherShutdownState
{
    private static readonly object Sync = new();
    private static readonly HashSet<Dispatcher> ShutdownDispatchers = [];
    private static readonly HashSet<Dispatcher> HookedDispatchers = [];

    public static bool HasShutdownStarted(this Dispatcher dispatcher)
    {
        lock (Sync)
        {
            if (HookedDispatchers.Add(dispatcher))
            {
                dispatcher.ShutdownStarted += (_, _) =>
                {
                    lock (Sync)
                    {
                        ShutdownDispatchers.Add(dispatcher);
                    }
                };
            }

            return ShutdownDispatchers.Contains(dispatcher);
        }
    }
}
