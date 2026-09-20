using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Midora.Avalonia.Windows;

/// <summary>
/// In-memory stand-in for Midora.Desktop's <c>TimelineGenerationPreset</c> /
/// <c>TimelineGenerationPresetInfo</c>. The real store persists JSON preset files.
/// </summary>
public sealed record TimelineGenerationPresetEntry(
    string Name,
    int MaximumCandidates,
    long? MaximumRelativeStartTick,
    bool CreateFirstFromInitialValues,
    TimelineGenerationPresetEntry.NoteFields? Note,
    TimelineGenerationPresetEntry.EventFields? Event)
{
    public sealed record NoteFields(
        double InitialVelocity,
        double InitialKey,
        double InitialGate,
        double InitialTick,
        string VelocityExpression,
        string KeyExpression,
        string GateExpression,
        string TickExpression);

    public sealed record EventFields(
        double InitialValue,
        double InitialTick,
        string ValueExpression,
        string TickExpression);

    public string Preview
    {
        get
        {
            string fields = Note is { } note
                ? $"Velocity: {note.VelocityExpression}\n  Initial: {Format(note.InitialVelocity)}\nKey: {note.KeyExpression}\n  Initial: {Format(note.InitialKey)}\nGate: {note.GateExpression}\n  Initial: {Format(note.InitialGate)}\nTick: {note.TickExpression}\n  Initial: {Format(note.InitialTick)}"
                : Event is { } point
                    ? $"Value: {point.ValueExpression}\n  Initial: {Format(point.InitialValue)}\nTick: {point.TickExpression}\n  Initial: {Format(point.InitialTick)}"
                    : string.Empty;
            return $"{fields}\n\nMaximum Candidates: {MaximumCandidates:N0}\nMaximum Relative Start Tick: {MaximumRelativeStartTick?.ToString(CultureInfo.InvariantCulture) ?? "None"}\nCreate initial object: {(CreateFirstFromInitialValues ? "Yes" : "No")}";
        }
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// Avalonia port of <c>Midora.Desktop.TimelineGenerationPresetDialog</c>. The WPF store is
/// replaced by an in-memory list shared with <see cref="TimelineGenerationDialog"/>; Add comes
/// from the parent dialog's Save Preset flow, Delete and Apply keep the WPF behavior.
/// </summary>
public partial class TimelineGenerationPresetDialog : Window
{
    private readonly IList<TimelineGenerationPresetEntry> _entries;
    private bool _closing;

    public TimelineGenerationPresetDialog()
        : this(notes: true, entries: null)
    {
    }

    public TimelineGenerationPresetDialog(bool notes, IList<TimelineGenerationPresetEntry>? entries = null)
    {
        InitializeComponent();
        _entries = entries ?? CreateDemoEntries(notes);
        Title = TitleText.Text = notes ? "Note Generation Presets" : "Event Generation Presets";
        PresetList.ItemsSource = _entries;
        UpdateSelectionState();
    }

    public TimelineGenerationPresetEntry? SelectedPreset { get; private set; }

    public bool Confirmed { get; private set; }

    /// <summary>Mirrors <c>TimelineGenerationPresetStore.OmittedPresetCount</c> for the result bar.</summary>
    public int OmittedPresetCount { get; set; }

    internal static ObservableCollection<TimelineGenerationPresetEntry> CreateDemoEntries(bool notes)
    {
        if (notes)
        {
            return
            [
                new("Ascending 16ths", 16, null, false,
                    new TimelineGenerationPresetEntry.NoteFields(1, 60, 24, 0, "=100", "=60 + i", "=24", "=i * 24"), null),
                new("Chord stack", 12, 1_024, true,
                    new TimelineGenerationPresetEntry.NoteFields(96, 48, 12, 0, "=96", "=48 + i % 12", "=12", "=i * 6"), null),
                new("Sparse accents", 64, null, false,
                    new TimelineGenerationPresetEntry.NoteFields(120, 72, 192, 0, "=i % 4 == 0 ? 120 : 64", "=72 + (i % 3) * 4", "=192", "=i * 48"), null)
            ];
        }

        return
        [
            new("CC sweep", 32, null, false,
                null, new TimelineGenerationPresetEntry.EventFields(0, 0, "=i * 4", "=i * 24")),
            new("Alternating values", 24, 512, true,
                null, new TimelineGenerationPresetEntry.EventFields(64, 0, "=i % 2 == 0 ? 127 : 0", "=i * 12")),
            new("Slow ramp", 128, null, false,
                null, new TimelineGenerationPresetEntry.EventFields(0, 0, "=i", "=i * 96"))
        ];
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    private void UpdateSelectionState()
    {
        bool hasSelection = PresetList.SelectedItem is TimelineGenerationPresetEntry;
        DeleteButton.IsEnabled = hasSelection;
        ApplyButton.IsEnabled = hasSelection;
        PreviewText.Text = PresetList.SelectedItem is TimelineGenerationPresetEntry selected
            ? selected.Preview
            : _entries.Count == 0
                ? "No presets are stored for this generation tool."
                : "Select a preset to preview it.";

        if (OmittedPresetCount != 0)
        {
            PreviewText.Text += $"\n\n{OmittedPresetCount} obsolete, invalid or unavailable preset file(s) were omitted and left unchanged. Event presets now use the CC editor display domain (numeric contract 2).";
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not TimelineGenerationPresetEntry selected) return;
        // WPF normalized and validated the preset through the store before accepting it.
        SelectedPreset = selected;
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
        if (PresetList.SelectedItem is not TimelineGenerationPresetEntry selected) return;
        MessageDialogResult result = await MessageDialog.ShowAsync(
            this,
            $"Delete preset '{selected.Name}'?",
            "Delete Generation Preset",
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

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancelClick(sender, e);
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
