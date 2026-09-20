using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class AboutDialog : Window
{
    // Placeholder for MidoraSoftwareVersion.ProductVersion, which lives in the WPF desktop project.
    private const string ProductVersionPlaceholder = "0.1.0-dev";

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {ProductVersionPlaceholder}";

        // Static sample values; the WPF dialog polls live process and audio worker counters once per second.
        MainCpuText.Text = "0.7%";
        MainWorkingSetText.Text = "412.5 MiB";
        MainPrivateMemoryText.Text = "268.3 MiB";
        WorkerNameText.Text = "Audio Worker(s) (1)";
        WorkerCpuText.Text = "0.9%";
        WorkerWorkingSetText.Text = "146.2 MiB";
        WorkerPrivateMemoryText.Text = "98.7 MiB";
        CombinedCpuText.Text = "1.6%";
        CombinedWorkingSetText.Text = "558.7 MiB";
        CombinedPrivateMemoryText.Text = "367.0 MiB";
    }

    public void SetResourceValuesUnavailable()
    {
        MainCpuText.Text = "Unavailable";
        MainWorkingSetText.Text = "Unavailable";
        MainPrivateMemoryText.Text = "Unavailable";
        WorkerNameText.Text = "Audio Worker(s)";
        WorkerCpuText.Text = "Unavailable";
        WorkerWorkingSetText.Text = "Unavailable";
        WorkerPrivateMemoryText.Text = "Unavailable";
        CombinedCpuText.Text = "Unavailable";
        CombinedWorkingSetText.Text = "Unavailable";
        CombinedPrivateMemoryText.Text = "Unavailable";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
