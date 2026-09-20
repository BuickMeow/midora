using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Midora.Avalonia.Platform;
using Midora.Avalonia.Windows;

namespace Midora.Avalonia;

public partial class MainWindow : Window
{
    private const double TitleBarHeight = 36;

    public MainWindow()
    {
        InitializeComponent();
        ApplyPlatformChrome();
        PopulateWindowsMenu();
    }

    private void PopulateWindowsMenu()
    {
        foreach (var group in WindowCatalog.Entries.GroupBy(entry => entry.Group))
        {
            var groupItem = new MenuItem { Header = group.Key };
            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.Name };
                item.Click += async (_, _) => await ShowCatalogWindowAsync(entry);
                groupItem.Items.Add(item);
            }

            WindowsMenu.Items.Add(groupItem);
        }
    }

    private async Task ShowCatalogWindowAsync(WindowCatalog.Entry entry)
    {
        try
        {
            await entry.Factory().ShowDialog(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Window '{entry.Name}' failed: {ex}");
        }
    }

    internal async Task<int> RunWindowSmokeAsync()
    {
        var failures = new List<string>();
        foreach (var entry in WindowCatalog.Entries)
        {
            Window? window = null;
            try
            {
                window = entry.Factory();
                window.Show();
                await Task.Delay(40);
                window.Close();
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Name}: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    window?.Close();
                }
                catch
                {
                    // The window never became usable; nothing else to release.
                }
            }
        }

        Console.Error.WriteLine(
            $"WINDOW-SMOKE total={WindowCatalog.Entries.Count} failures={failures.Count}");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine("FAIL " + failure);
        }

        return failures.Count == 0 ? 0 : 2;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RecenterTrafficLights();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            RecenterTrafficLights();
        }
    }

    private void ApplyPlatformChrome()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        // macOS: keep the native traffic lights inside the extended title bar.
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        CaptionButtons.IsVisible = false;
        TitleBarContent.Margin = new Thickness(72, 0, 0, 0);
    }

    private void RecenterTrafficLights()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var handle = TryGetPlatformHandle();
        if (handle is not null)
        {
            MacWindowChrome.CenterTrafficLights(handle.Handle, TitleBarHeight);
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
}
