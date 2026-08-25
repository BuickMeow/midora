using Midora.Audio.Bass;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Midora.Desktop;

public partial class AboutDialog : Window
{
    private readonly DispatcherTimer _refreshTimer;
    private ResourceSample? _previousSample;

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {MidoraSoftwareVersion.ProductVersion}";
        _refreshTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshResourceValues();
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e) => RefreshResourceValues();

    private void RefreshResourceValues()
    {
        try
        {
            ResourceSample sample = CaptureSample();
            MainWorkingSetText.Text = FormatBytes(sample.MainWorkingSetBytes);
            MainPrivateMemoryText.Text = FormatBytes(sample.MainPrivateMemoryBytes);
            WorkerNameText.Text = $"Audio Worker(s) ({sample.WorkerCount.ToString(CultureInfo.InvariantCulture)})";
            WorkerWorkingSetText.Text = FormatBytes(sample.WorkerWorkingSetBytes);
            WorkerPrivateMemoryText.Text = FormatBytes(sample.WorkerPrivateMemoryBytes);
            CombinedWorkingSetText.Text = FormatBytes(SaturatingAdd(
                sample.MainWorkingSetBytes,
                sample.WorkerWorkingSetBytes));
            CombinedPrivateMemoryText.Text = FormatBytes(SaturatingAdd(
                sample.MainPrivateMemoryBytes,
                sample.WorkerPrivateMemoryBytes));

            if (_previousSample is ResourceSample previous
                && sample.Timestamp > previous.Timestamp)
            {
                double elapsedSeconds = (sample.Timestamp - previous.Timestamp) /
                    (double)Stopwatch.Frequency;
                double mainCpu = CalculateCpuPercent(
                    sample.MainProcessorTime,
                    previous.MainProcessorTime,
                    elapsedSeconds);
                double workerCpu = CalculateCpuPercent(
                    sample.WorkerProcessorTime,
                    previous.WorkerProcessorTime,
                    elapsedSeconds);
                MainCpuText.Text = FormatCpu(mainCpu);
                WorkerCpuText.Text = FormatCpu(workerCpu);
                CombinedCpuText.Text = FormatCpu(Math.Min(100, mainCpu + workerCpu));
            }
            else
            {
                MainCpuText.Text = "Measuring…";
                WorkerCpuText.Text = "Measuring…";
                CombinedCpuText.Text = "Measuring…";
            }
            _previousSample = sample;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException
                or UnauthorizedAccessException)
        {
            SetResourceValuesUnavailable();
        }
    }

    private static ResourceSample CaptureSample()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        AudioWorkerResourceSnapshot workers = AudioWorkerProcessGroup.CaptureResources();
        return new(
            Stopwatch.GetTimestamp(),
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            workers.TotalProcessorTime,
            workers.WorkingSetBytes,
            workers.PrivateMemoryBytes,
            workers.ActiveProcessCount);
    }

    private static double CalculateCpuPercent(
        TimeSpan current,
        TimeSpan previous,
        double elapsedSeconds)
    {
        double processorSeconds = Math.Max(0, (current - previous).TotalSeconds);
        double capacity = elapsedSeconds * Math.Max(1, Environment.ProcessorCount);
        return capacity <= 0 ? 0 : Math.Min(100, processorSeconds / capacity * 100);
    }

    private static string FormatCpu(double value) =>
        value.ToString("0.0'%'", CultureInfo.InvariantCulture);

    private void SetResourceValuesUnavailable()
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

    private static string FormatBytes(long bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = kibibyte * 1024;
        const double gibibyte = mebibyte * 1024;
        return bytes >= gibibyte
            ? (bytes / gibibyte).ToString("0.00 'GiB'", CultureInfo.InvariantCulture)
            : (bytes / mebibyte).ToString("0.0 'MiB'", CultureInfo.InvariantCulture);
    }

    private static long SaturatingAdd(long left, long right) =>
        right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private readonly record struct ResourceSample(
        long Timestamp,
        TimeSpan MainProcessorTime,
        long MainWorkingSetBytes,
        long MainPrivateMemoryBytes,
        TimeSpan WorkerProcessorTime,
        long WorkerWorkingSetBytes,
        long WorkerPrivateMemoryBytes,
        int WorkerCount);
}
