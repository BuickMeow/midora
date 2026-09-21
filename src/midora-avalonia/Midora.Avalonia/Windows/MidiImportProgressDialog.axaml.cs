using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Midora.Application;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Determinate progress for <c>Open MIDI as New Project</c>. The streaming import reports byte and
/// event counts, so the dialog mirrors the WPF import progress and can cancel the task. Closing the
/// window cancels as well, and the caller stays responsible for the Project state.
/// </summary>
public partial class MidiImportProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _completed;

    public MidiImportProgressDialog()
        : this("Sample.mid")
    {
    }

    private MidiImportProgressDialog(string fileName)
    {
        InitializeComponent();
        Title = "Open MIDI as New Project";
        PhaseText.Text = $"Preparing {fileName}…";
        DetailText.Text = string.Empty;
        Closed += (_, _) => _cancellation.Cancel();
    }

    public CancellationToken CancellationToken => _cancellation.Token;

    public static MidiImportProgressDialog Show(Window? owner, string fileName)
    {
        MidiImportProgressDialog dialog = new(fileName);
        if (owner is { IsVisible: true })
        {
            dialog.Show(owner);
        }
        else
        {
            dialog.Show();
        }

        return dialog;
    }

    public void Report(MidiProjectImportProgress progress)
    {
        if (_completed)
        {
            return;
        }

        PhaseText.Text = progress.Phase switch
        {
            MidiProjectImportPhase.ScanningSource => "Scanning MIDI source…",
            MidiProjectImportPhase.ImportingEvents => "Importing MIDI events…",
            MidiProjectImportPhase.ValidatingProject => "Validating project…",
            MidiProjectImportPhase.FinalizingProject => "Finalizing project…",
            _ => "Completed."
        };
        double fraction = double.IsFinite(progress.Fraction)
            ? Math.Clamp(progress.Fraction, 0, 1)
            : 0;
        ImportProgress.Value = fraction * 100;
        DetailText.Text = progress.Phase switch
        {
            MidiProjectImportPhase.ScanningSource when progress.TotalSourceBytes > 0 =>
                $"{progress.ProcessedSourceBytes / (1024.0 * 1024.0):F1} / "
                + $"{progress.TotalSourceBytes / (1024.0 * 1024.0):F1} MB",
            MidiProjectImportPhase.ImportingEvents when progress.TotalEventCount > 0 =>
                $"{progress.ProcessedEventCount:N0} / {progress.TotalEventCount:N0} events",
            _ => string.Empty
        };
    }

    public void Complete()
    {
        _completed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        PhaseText.Text = "Cancelling…";
        _cancellation.Cancel();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => OnCancelClick(sender, e);

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
