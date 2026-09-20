using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Midora.Avalonia.Windows;

/// <summary>Mode selector shared with <see cref="SplitNotesDialog"/>.</summary>
public enum NoteSplitMode
{
    FixedPieceLength,
    MaximumPieceCount,
    Expression
}

/// <summary>
/// In-memory stand-in for Midora.Desktop's <c>NoteSplitPreset</c> / <c>NoteSplitPresetInfo</c>.
/// The real store persists JSON preset files under the program data root.
/// </summary>
public sealed record NoteSplitPresetEntry(
    string Name,
    NoteSplitMode Mode,
    long FixedLengthTicks,
    int MaximumPieceCount,
    string Expression,
    int MaximumCuts)
{
    public string Preview => Mode switch
    {
        NoteSplitMode.FixedPieceLength => $"Fixed Piece Length\n{FixedLengthTicks} Ticks",
        NoteSplitMode.MaximumPieceCount => $"Maximum Piece Count\n{MaximumPieceCount} pieces",
        NoteSplitMode.Expression => $"Expression\n{Expression}\nMaximum Cuts: {MaximumCuts}",
        _ => "Unsupported preset"
    };
}

/// <summary>
/// Avalonia port of <c>Midora.Desktop.NoteSplitPresetDialog</c>. The WPF store is replaced by an
/// in-memory list that <see cref="SplitNotesDialog"/> can also seed; Add comes from the parent
/// dialog's Save Preset flow, Delete and Apply keep the WPF behavior.
/// </summary>
public partial class NoteSplitPresetDialog : Window
{
    private readonly IList<NoteSplitPresetEntry> _entries;
    private bool _closing;

    public NoteSplitPresetDialog()
        : this(null)
    {
    }

    public NoteSplitPresetDialog(IList<NoteSplitPresetEntry>? entries)
    {
        InitializeComponent();
        _entries = entries ?? CreateDemoEntries();
        PresetList.ItemsSource = _entries;
        UpdateSelectionState();
    }

    public NoteSplitPresetEntry? SelectedPreset { get; private set; }

    public bool Confirmed { get; private set; }

    internal static ObservableCollection<NoteSplitPresetEntry> CreateDemoEntries() =>
    [
        new("Halves", NoteSplitMode.FixedPieceLength, 384, 2, "=192", 65_535),
        new("Eighths", NoteSplitMode.FixedPieceLength, 192, 2, "=192", 65_535),
        new("Two equal pieces", NoteSplitMode.MaximumPieceCount, 192, 2, "=192", 65_535),
        new("Growing pieces", NoteSplitMode.Expression, 192, 2, "=192 + i * 48", 65_535),
        new("Four cut limit", NoteSplitMode.Expression, 192, 2, "=96 + (i % 4) * 24", 4)
    ];

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        bool hasSelection = PresetList.SelectedItem is NoteSplitPresetEntry;
        DeleteButton.IsEnabled = hasSelection;
        ApplyButton.IsEnabled = hasSelection;
        PreviewText.Text = PresetList.SelectedItem is NoteSplitPresetEntry selected
            ? selected.Preview
            : _entries.Count == 0
                ? "No Note Split presets are stored."
                : "Select a preset to preview it.";
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not NoteSplitPresetEntry selected) return;
        SelectedPreset = selected;
        // WPF set DialogResult = true; Avalonia closes and the caller reads Confirmed.
        Confirmed = true;
        _closing = true;
        Close();
    }

    private void OnPresetListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        OnApplyClick(sender, e);
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not NoteSplitPresetEntry selected) return;
        MessageDialogResult result = await MessageDialog.ShowAsync(
            this,
            $"Delete Note Split preset '{selected.Name}'?",
            "Delete Note Split Preset",
            MessageDialogButtons.YesNo,
            MessageDialogIcon.Warning);
        if (result != MessageDialogResult.Yes) return;

        _entries.Remove(selected);
        UpdateSelectionState();
    }

    // WPF installed a one-item-per-notch wheel router; Avalonia needs the ScrollViewer lookup.
    private void OnPresetListPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        ScrollViewer? scroll = PresetList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        double step = PresetList.ContainerFromIndex(0)?.Bounds.Height ?? 0;
        if (step <= 0) step = 20;
        double maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        double offset = Math.Clamp(scroll.Offset.Y - Math.Sign(e.Delta.Y) * step, 0, maximum);
        scroll.Offset = new Vector(scroll.Offset.X, offset);
        e.Handled = true;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        SelectedPreset = null;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
