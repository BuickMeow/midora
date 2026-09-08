using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop;

public partial class AllTracksView : UserControl
{
    public AllTracksView() { InitializeComponent(); }
    public event EventHandler<TimelineRulerEventArgs>? PlaybackCursorRequested;

    internal bool TryNavigate(Point point, MouseButton button)
    {
        if (button != MouseButton.Left || DataContext is not AllTracksWorkspaceViewModel
            || !Timeline.TryGetNavigationTick(point, out long tick)) return false;
        PlaybackCursorRequested?.Invoke(this, new(tick));
        return true;
    }

    private void OnReadOnlyMouseDown(object sender, MouseButtonEventArgs e)
    {
        OnFollowMouseDown(sender, e);
        if (e.ChangedButton == MouseButton.Middle) return; // Keep normal viewport panning.
        Timeline.Focus();
        TryNavigate(e.GetPosition(Timeline), e.ChangedButton);
        e.Handled = true; // No marquee, note editing, scrub or delayed editing menu.
    }
    private void OnFollowMouseDown(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportPreviewMouseDown(sender, e);
    private void OnFollowMouseUp(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportPreviewMouseUp(sender, e);
    private void OnFollowLostMouseCapture(object sender, MouseEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportLostMouseCapture(sender, e);
    private void OnFollowOverviewMouseWheel(object sender, MouseWheelEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowOverviewPreviewMouseWheel(sender, e);
    private void OnZoomOut(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.TickSpan = Math.Min(long.MaxValue / 2, vm.TickSpan) * 2; }
    private void OnZoomIn(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.TickSpan /= 2; }
    private void OnFit(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) { vm.StartTick = 0; vm.TickSpan = vm.ExtentEndTick; vm.FirstLane = 0; vm.LaneHeight = Math.Max(3, Timeline.ActualHeight / 128); } }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Timeline?.Focus()));
    private void OnRefresh(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.RetryBuild(); }
    private void OnCancelBuild(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.CancelBuild(); }
    private void OnHelp(object sender, RoutedEventArgs e) => OnionSettingsDialog.ShowHelp(Window.GetWindow(this));
}
