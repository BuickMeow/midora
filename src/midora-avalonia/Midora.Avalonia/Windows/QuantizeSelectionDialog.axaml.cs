using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public sealed record QuantizeGridChoice(
    string Label,
    int Numerator,
    int Denominator,
    bool IsCustom = false);

/// <summary>
/// Placeholder for the Midora.Application quantize grid; carries the resolved fraction or
/// the custom tick step selected by the dialog.
/// </summary>
public sealed record TimelineQuantizeGrid(int Numerator, int Denominator, bool IsCustom, long CustomTicks)
{
    public static TimelineQuantizeGrid MusicalFraction(int numerator, int denominator) =>
        new(numerator, denominator, false, 0);

    public static TimelineQuantizeGrid FromCustomTicks(long ticks) => new(0, 0, true, ticks);
}

public enum NoteQuantizeMode
{
    StartOnly,
    StartAndEnd
}

public sealed record NoteQuantizeOptions(TimelineQuantizeGrid Grid, NoteQuantizeMode Mode);

public partial class QuantizeSelectionDialog : Window
{
    public static IReadOnlyList<QuantizeGridChoice> GridChoices { get; } =
    [
        new("1/1 · Whole", 1, 1),
        new("1/2 · Half", 1, 2),
        new("1/3 · Half triplet", 1, 3),
        new("1/4 · Quarter", 1, 4),
        new("1/6 · Quarter triplet", 1, 6),
        new("3/16 · Dotted eighth", 3, 16),
        new("1/8 · Eighth", 1, 8),
        new("1/12 · Eighth triplet", 1, 12),
        new("3/32 · Dotted sixteenth", 3, 32),
        new("1/16 · Sixteenth", 1, 16),
        new("1/24 · Sixteenth triplet", 1, 24),
        new("3/64 · Dotted thirty-second", 3, 64),
        new("1/32 · Thirty-second", 1, 32),
        new("1/48 · Thirty-second triplet", 1, 48),
        new("1/64 · Sixty-fourth", 1, 64),
        new("1/128", 1, 128),
        new("1/256", 1, 256),
        new("Custom Ticks", 0, 0, IsCustom: true)
    ];

    private readonly bool _isNoteSelection;
    private readonly IReadOnlyList<QuantizeGridChoice> _gridChoices;

    public QuantizeSelectionDialog()
        : this(true)
    {
    }

    public QuantizeSelectionDialog(
        bool isNoteSelection,
        QuantizeGridChoice? initialGrid = null)
    {
        _isNoteSelection = isNoteSelection;
        InitializeComponent();
        TitleText.Text = isNoteSelection ? "Quantize Notes" : "Quantize Events";
        Title = TitleText.Text;
        NoteModeRow.IsVisible = isNoteSelection;
        _gridChoices = CreateGridChoices(initialGrid);
        GridBox.ItemsSource = _gridChoices;
        GridBox.SelectedItem = ResolveInitialGridChoice(_gridChoices, initialGrid);
    }

    /// <summary>
    /// Result semantics: a non-null value means Apply was confirmed. Avalonia has no
    /// Window.DialogResult, so <c>Close()</c> plus these properties carries the outcome.
    /// </summary>
    public TimelineQuantizeGrid? Grid { get; private set; }

    public NoteQuantizeOptions? NoteOptions { get; private set; }

    public static IReadOnlyList<QuantizeGridChoice> CreateGridChoices(
        QuantizeGridChoice? initialGrid)
    {
        if (initialGrid is not { IsCustom: false, Numerator: > 0, Denominator: > 0 } initial
            || GridChoices.Any(value =>
                !value.IsCustom
                && value.Numerator == initial.Numerator
                && value.Denominator == initial.Denominator))
        {
            return GridChoices;
        }

        QuantizeGridChoice[] choices = new QuantizeGridChoice[GridChoices.Count + 1];
        int customIndex = GridChoices.Count - 1;
        for (int index = 0; index < customIndex; index++) choices[index] = GridChoices[index];
        choices[customIndex] = new($"{initial.Numerator}/{initial.Denominator}", initial.Numerator, initial.Denominator);
        choices[^1] = GridChoices[^1];
        return choices;
    }

    public static QuantizeGridChoice ResolveInitialGridChoice(
        IReadOnlyList<QuantizeGridChoice> choices,
        QuantizeGridChoice? initialGrid)
    {
        ArgumentNullException.ThrowIfNull(choices);
        QuantizeGridChoice initial = initialGrid is { IsCustom: false, Numerator: > 0, Denominator: > 0 } candidate
            ? candidate
            : choices.First(value =>
                !value.IsCustom && value.Numerator == 1 && value.Denominator == 16);
        return choices.FirstOrDefault(value =>
                !value.IsCustom
                && value.Numerator == initial.Numerator
                && value.Denominator == initial.Denominator)
            ?? choices.First(value =>
                !value.IsCustom && value.Numerator == 1 && value.Denominator == 16);
    }

    public static TimelineQuantizeGrid ParseGrid(QuantizeGridChoice? choice, string? customTicksText)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!choice.IsCustom)
        {
            if (choice.Numerator <= 0 || choice.Denominator <= 0)
                throw new ArgumentException("The selected musical grid is invalid.");
            return TimelineQuantizeGrid.MusicalFraction(choice.Numerator, choice.Denominator);
        }
        if (!long.TryParse(
                customTicksText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long ticks)
            || ticks <= 0)
        {
            throw new ArgumentException("Custom Grid must be a positive Int64 Tick value.");
        }
        return TimelineQuantizeGrid.FromCustomTicks(ticks);
    }

    private void OnGridChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        CustomTicksRow.IsVisible = GridBox.SelectedItem is QuantizeGridChoice { IsCustom: true };
        ClearValidation();
    }

    private void OnInputChanged(object? sender, TextChangedEventArgs e) => ClearValidation();

    private void OnInputSelectionChanged(object? sender, SelectionChangedEventArgs e) => ClearValidation();

    private void ClearValidation()
    {
        if (!IsInitialized || ValidationText is null) return;
        ValidationText.Text = string.Empty;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TimelineQuantizeGrid grid = ParseGrid(
                GridBox.SelectedItem as QuantizeGridChoice,
                CustomTicksBox.Text);
            Grid = grid;
            NoteOptions = _isNoteSelection
                ? new NoteQuantizeOptions(
                    grid,
                    NoteModeBox.SelectedIndex == 1
                        ? NoteQuantizeMode.StartAndEnd
                        : NoteQuantizeMode.StartOnly)
                : null;
            Close();
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
