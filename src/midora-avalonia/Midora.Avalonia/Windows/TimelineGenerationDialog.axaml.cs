using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Avalonia port of <c>Midora.Desktop.TimelineGenerationDialog</c>. Numeric parsing, range
/// checks, the initial-value defaults, the limit toggle, the validation result bar, preset
/// application and Create/Cancel semantics are ported. Expressions are not compiled here, exactly
/// like the WPF dialog, which only validates settings at this stage.
/// </summary>
public partial class TimelineGenerationDialog : Window
{
    // Mirrors TimelineGenerationLimits.MaximumCandidates.
    public const int MaximumCandidatesLimit = 16_777_216;

    private readonly bool _notes;
    private readonly double _pointMinimum;
    private readonly ObservableCollection<TimelineGenerationPresetEntry> _presets;

    private bool _initialized;
    private bool _closing;

    public TimelineGenerationDialog()
        : this(notes: true, baseTick: 0)
    {
    }

    public TimelineGenerationDialog(bool notes, long baseTick, double pointMinimum = 0, double pointMaximum = 127)
    {
        if (baseTick < 0 || baseTick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(baseTick));
        if (!double.IsFinite(pointMinimum) || !double.IsFinite(pointMaximum) || pointMaximum < pointMinimum)
            throw new ArgumentOutOfRangeException(nameof(pointMinimum));

        _notes = notes;
        _pointMinimum = pointMinimum;
        _presets = TimelineGenerationPresetDialog.CreateDemoEntries(notes);

        InitializeComponent();
        Title = TitleText.Text = notes ? "Batch Create Notes" : "Batch Create Events";
        ContextText.Text = notes
            ? "Create notes in the current piano roll. Existing notes at the same Tick and Key win; only surviving new notes are selected."
            : FormattableString.Invariant($"Create points in the current lane (value range {pointMinimum}–{pointMaximum}). At the same Tick and target, the last generated point wins.");
        VelocityRow.IsVisible = notes;
        KeyRow.IsVisible = notes;
        GateRow.IsVisible = notes;
        PointValueRow.IsVisible = !notes;
        BaseTickBox.Text = baseTick.ToString(CultureInfo.InvariantCulture);
        InitialValueBox.Text = Format(pointMinimum);
        _initialized = true;
        Opened += (_, _) => (notes ? VelocityBox : PointValueBox).Focus();
    }

    public NoteOptions? NoteResult { get; private set; }

    public EventOptions? EventResult { get; private set; }

    public bool Confirmed { get; private set; }

    public sealed record NoteOptions(
        long BaseTick,
        int MaximumCandidates,
        long? MaximumRelativeStartTick,
        bool CreateFirstFromInitialValues,
        double InitialVelocity,
        double InitialKey,
        double InitialGate,
        double InitialTick,
        string VelocityExpression,
        string KeyExpression,
        string GateExpression,
        string TickExpression);

    public sealed record EventOptions(
        long BaseTick,
        int MaximumCandidates,
        long? MaximumRelativeStartTick,
        bool CreateFirstFromInitialValues,
        double InitialValue,
        double InitialTick,
        string ValueExpression,
        string TickExpression);

    internal NoteOptions CaptureNoteOptions() => new(
        ParseBaseTick(BaseTickBox.Text),
        ParseMaximumCandidates(MaximumCandidatesBox.Text),
        ParseMaximumRelativeStart(MaximumRelativeTickBox.Text, LimitRelativeTickBox.IsChecked == true),
        CreateInitialBox.IsChecked == true,
        ParseInitial(InitialVelocityBox.Text, 1, "Velocity"),
        ParseInitial(InitialKeyBox.Text, 0, "Key"),
        ParseInitial(InitialGateBox.Text, 1, "Gate"),
        ParseInitial(InitialTickBox.Text, 0, "Tick"),
        VelocityBox.Text ?? string.Empty,
        KeyBox.Text ?? string.Empty,
        GateBox.Text ?? string.Empty,
        TickBox.Text ?? string.Empty);

    internal EventOptions CaptureEventOptions() => new(
        ParseBaseTick(BaseTickBox.Text),
        ParseMaximumCandidates(MaximumCandidatesBox.Text),
        ParseMaximumRelativeStart(MaximumRelativeTickBox.Text, LimitRelativeTickBox.IsChecked == true),
        CreateInitialBox.IsChecked == true,
        ParseInitial(InitialValueBox.Text, _pointMinimum, "Value"),
        ParseInitial(InitialTickBox.Text, 0, "Tick"),
        PointValueBox.Text ?? string.Empty,
        TickBox.Text ?? string.Empty);

    internal static long ParseBaseTick(string? text)
    {
        if (!long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value == long.MaxValue)
        {
            throw new ArgumentException("Base Tick must be within 0–9,223,372,036,854,775,806.");
        }

        return value;
    }

    internal static int ParseMaximumCandidates(string? text)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value is < 1 or > MaximumCandidatesLimit)
        {
            throw new ArgumentException($"Maximum Candidates must be within 1–{MaximumCandidatesLimit:N0}.");
        }

        return value;
    }

    internal static long? ParseMaximumRelativeStart(string? text, bool enabled)
    {
        if (!enabled) return null;
        if (!long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
        {
            throw new ArgumentException("Maximum Relative Start Tick must be a non-negative Int64 Tick value.");
        }

        return value;
    }

    internal static double ParseInitial(string? text, double fallback, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
        {
            throw new ArgumentException($"{label} Initial must be a finite number (use a period for decimals).");
        }

        return value;
    }

    private void OnValidateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_notes) _ = CaptureNoteOptions();
            else _ = CaptureEventOptions();
            SetStatus(
                "Expressions and settings are valid. Generation checks every candidate and may still reject runtime errors or resource limits.",
                "Brush.Success");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetStatus(exception.Message, "Brush.Red.Hover");
        }
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e) => TryCreate();

    private void OnExpressionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        e.Handled = true;
        TryCreate();
    }

    private void TryCreate()
    {
        try
        {
            if (_notes) NoteResult = CaptureNoteOptions();
            else EventResult = CaptureEventOptions();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetStatus(exception.Message, "Brush.Red.Hover");
            return;
        }

        Confirmed = true;
        _closing = true;
        Close();
    }

    private async void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        TimelineGenerationHelpDialog dialog = new(_notes);
        await dialog.ShowDialog(this);
    }

    private async void OnPresetsClick(object? sender, RoutedEventArgs e)
    {
        TimelineGenerationPresetDialog dialog = new(_notes, _presets);
        await dialog.ShowDialog(this);
        if (dialog.SelectedPreset is not TimelineGenerationPresetEntry preset) return;
        ApplyPreset(preset);
        SetStatus($"Preset '{preset.Name}' loaded. Base Tick and the target are unchanged.", "Brush.Text.Tertiary");
    }

    private async void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        TimelineGenerationPresetEntry captured;
        try
        {
            captured = CapturePreset("Preset");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            SetStatus(exception.Message, "Brush.Red.Hover");
            return;
        }

        string? name = await TextInputDialog.ShowAsync(
            this,
            "Save Generation Preset",
            "Enter a unique preset name.",
            string.Empty,
            ValidatePresetName);
        if (name is null) return;

        name = name.Trim();
        _presets.Add(captured with { Name = name });
        SetStatus($"Preset '{name}' saved.", "Brush.Text.Tertiary");
    }

    private string? ValidatePresetName(string value)
    {
        string name = (value ?? string.Empty).Trim();
        if (name.Length is < 1 or > 100 || name.EndsWith('.'))
        {
            return "Preset names must contain 1–100 filename-safe characters and cannot end with a period.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Preset names must contain 1–100 filename-safe characters and cannot end with a period.";
        }

        if (_presets.Any(preset => string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A preset named '{name}' already exists.";
        }

        return null;
    }

    private TimelineGenerationPresetEntry CapturePreset(string name)
    {
        if (_notes)
        {
            NoteOptions note = CaptureNoteOptions();
            return new TimelineGenerationPresetEntry(
                name,
                note.MaximumCandidates,
                note.MaximumRelativeStartTick,
                note.CreateFirstFromInitialValues,
                new TimelineGenerationPresetEntry.NoteFields(
                    note.InitialVelocity,
                    note.InitialKey,
                    note.InitialGate,
                    note.InitialTick,
                    note.VelocityExpression,
                    note.KeyExpression,
                    note.GateExpression,
                    note.TickExpression),
                null);
        }

        EventOptions point = CaptureEventOptions();
        return new TimelineGenerationPresetEntry(
            name,
            point.MaximumCandidates,
            point.MaximumRelativeStartTick,
            point.CreateFirstFromInitialValues,
            null,
            new TimelineGenerationPresetEntry.EventFields(
                point.InitialValue,
                point.InitialTick,
                point.ValueExpression,
                point.TickExpression));
    }

    internal void ApplyPreset(TimelineGenerationPresetEntry preset)
    {
        MaximumCandidatesBox.Text = preset.MaximumCandidates.ToString(CultureInfo.InvariantCulture);
        LimitRelativeTickBox.IsChecked = preset.MaximumRelativeStartTick.HasValue;
        MaximumRelativeTickBox.Text = (preset.MaximumRelativeStartTick ?? 0).ToString(CultureInfo.InvariantCulture);
        CreateInitialBox.IsChecked = preset.CreateFirstFromInitialValues;
        if (preset.Note is { } note)
        {
            VelocityBox.Text = note.VelocityExpression;
            KeyBox.Text = note.KeyExpression;
            GateBox.Text = note.GateExpression;
            TickBox.Text = note.TickExpression;
            InitialVelocityBox.Text = Format(note.InitialVelocity);
            InitialKeyBox.Text = Format(note.InitialKey);
            InitialGateBox.Text = Format(note.InitialGate);
            InitialTickBox.Text = Format(note.InitialTick);
        }
        else if (preset.Event is { } point)
        {
            PointValueBox.Text = point.ValueExpression;
            TickBox.Text = point.TickExpression;
            InitialValueBox.Text = Format(point.InitialValue);
            InitialTickBox.Text = Format(point.InitialTick);
        }

        SetStatus("Not validated.", "Brush.Text.Tertiary");
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Used by both TextBox.TextChanged (TextChangedEventArgs) and CheckBox Checked/Unchecked.
    private void OnInputChanged(object? sender, RoutedEventArgs e)
    {
        if (_initialized) SetStatus("Not validated.", "Brush.Text.Tertiary");
    }

    private void OnLimitChanged(object? sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        MaximumRelativeTickBox.IsEnabled = LimitRelativeTickBox.IsChecked == true;
        SetStatus("Not validated.", "Brush.Text.Tertiary");
    }

    private void SetStatus(string text, string brushKey)
    {
        ValidationText.Text = text;
        if (this.TryFindResource(brushKey, out object? value) && value is IBrush brush)
        {
            ValidationText.Foreground = brush;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        NoteResult = null;
        EventResult = null;
        Confirmed = false;
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
