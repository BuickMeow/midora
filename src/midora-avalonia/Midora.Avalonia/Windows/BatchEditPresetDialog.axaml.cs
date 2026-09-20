using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Midora.Avalonia.Windows;

/// <summary>
/// In-memory stand-in for Midora.Desktop's <c>BatchEditPreset</c> / <c>BatchEditPresetInfo</c>.
/// The real store persists JSON preset files under the program data root; the Avalonia port shares
/// one collection between <see cref="BatchEditDialog"/> and this dialog so saved presets appear in
/// the list during the session.
/// </summary>
public sealed record BatchEditPresetEntry(
    string Name,
    bool NoteKind,
    string Velocity,
    string PointValue,
    string KeyNumber,
    string Gate,
    string Tick)
{
    public string Preview => NoteKind
        ? $"Velocity: {Show(Velocity)}\nKey Number: {Show(KeyNumber)}\nGate: {Show(Gate)}\nTick: {Show(Tick)}"
        : $"Point Value: {Show(PointValue)}\nTick: {Show(Tick)}";

    private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "(unchanged)" : value;
}

/// <summary>
/// Avalonia port of <c>Midora.Desktop.BatchEditPresetDialog</c>. Load/select/preview/delete/apply and
/// the double-click shortcut follow the WPF dialog; the JSON preset store and its
/// "obsolete preset files were omitted" notice are replaced by the shared in-memory list.
/// </summary>
public partial class BatchEditPresetDialog : Window
{
    private readonly ObservableCollection<BatchEditPresetEntry> _presets;
    private readonly bool _noteKind;

    public BatchEditPresetDialog()
        : this(presets: null, noteKind: true)
    {
    }

    public BatchEditPresetDialog(ObservableCollection<BatchEditPresetEntry>? presets, bool noteKind)
    {
        _presets = presets ?? CreateDemoEntries();
        _noteKind = noteKind;
        InitializeComponent();
        Reload();
    }

    public BatchEditPresetEntry? SelectedPreset { get; private set; }

    /// <summary>Avalonia has no DialogResult: callers read <see cref="Confirmed"/> after the dialog closes.</summary>
    public bool Confirmed { get; private set; }

    internal static ObservableCollection<BatchEditPresetEntry> CreateDemoEntries() =>
    [
        new("Loud accents", true, "=Clamp(v0 * 1.25, 1, 127)", string.Empty, string.Empty, "*1.1", "+0"),
        new("Half gate", true, string.Empty, string.Empty, string.Empty, "*0.5", string.Empty),
        new("Soft layer", true, "=Clamp(v0 * 0.8, 1, 127)", string.Empty, string.Empty, string.Empty, "+0"),
        new("Slow fade steps", false, string.Empty, "*0.9", string.Empty, string.Empty, "=t0 + 120"),
        new("Hold and repeat", false, string.Empty, "+32", string.Empty, string.Empty, "+240")
    ];

    private void Reload()
    {
        BatchEditPresetEntry[] visible = _presets.Where(entry => entry.NoteKind == _noteKind).ToArray();
        PresetList.ItemsSource = visible;
        UpdatePreview();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        PreviewText.Text = PresetList.SelectedItem is BatchEditPresetEntry preset
            ? preset.Preview
            : _presets.Any(entry => entry.NoteKind == _noteKind)
                ? "Select a preset to preview it."
                : "No presets are stored in this category.";
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not BatchEditPresetEntry preset)
        {
            return;
        }

        SelectedPreset = preset;
        Confirmed = true;
        Close();
    }

    private void OnPresetListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
        {
            return;
        }

        OnApplyClick(sender, e);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not BatchEditPresetEntry preset)
        {
            return;
        }

        MessageDialogResult result = await MessageDialog.ShowAsync(
            this,
            $"Delete batch edit preset '{preset.Name}'?",
            "Delete Preset",
            MessageDialogButtons.YesNo,
            MessageDialogIcon.Warning);
        if (result != MessageDialogResult.Yes)
        {
            return;
        }

        _presets.Remove(preset);
        Reload();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SelectedPreset = null;
        Confirmed = false;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
