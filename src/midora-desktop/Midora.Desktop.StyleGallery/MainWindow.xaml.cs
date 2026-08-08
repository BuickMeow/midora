using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Midora.Desktop.StyleGallery;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnWindowMessage);
            _windowSource = null;
        }

        base.OnClosed(e);
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        var windowChrome = WindowChrome.GetWindowChrome(this);
        if (windowChrome is not null)
        {
            windowChrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(6);
        }

        MaximizeGlyph.Data = (Geometry)FindResource(
            WindowState == WindowState.Maximized
                ? "WindowControl.Restore"
                : "WindowControl.Maximize");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmGetMinMaxInfo &&
            ApplyMonitorWorkArea(windowHandle, lParam, MinWidth, MinHeight))
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool ApplyMonitorWorkArea(
        IntPtr windowHandle,
        IntPtr minMaxInfoPointer,
        double minimumWidth,
        double minimumHeight)
    {
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        var monitorBounds = monitorInfo.MonitorBounds;
        var workArea = monitorInfo.WorkArea;

        minMaxInfo.MaxPosition.X = workArea.Left - monitorBounds.Left;
        minMaxInfo.MaxPosition.Y = workArea.Top - monitorBounds.Top;
        minMaxInfo.MaxSize.X = workArea.Right - workArea.Left;
        minMaxInfo.MaxSize.Y = workArea.Bottom - workArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;

        var dpi = GetDpiForWindow(windowHandle);
        var dpiScale = (dpi == 0 ? 96u : dpi) / 96d;
        minMaxInfo.MinTrackSize.X = Math.Max(
            minMaxInfo.MinTrackSize.X,
            (int)Math.Ceiling(minimumWidth * dpiScale));
        minMaxInfo.MinTrackSize.Y = Math.Max(
            minMaxInfo.MinTrackSize.Y,
            (int)Math.Ceiling(minimumHeight * dpiScale));

        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
        return true;
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle MonitorBounds;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}
