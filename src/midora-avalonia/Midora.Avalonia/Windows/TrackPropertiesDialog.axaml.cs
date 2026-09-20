using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

public partial class TrackPropertiesDialog : Window
{
    private static readonly (byte Red, byte Green, byte Blue)[] PlaceholderPalette =
    [
        (0xE5, 0x48, 0x4D),
        (0xE8, 0xB3, 0x4B),
        (0x58, 0xC4, 0x87),
        (0x62, 0xA6, 0xF6),
        (0xC7, 0x8A, 0xFF)
    ];

    private readonly bool _supportsInheritedColor;
    private (byte Red, byte Green, byte Blue) _selectedColor = PlaceholderPalette[0];
    private int _paletteIndex;

    public TrackPropertiesDialog()
        : this("Track Properties", "Track 1", supportsInheritedColor: true)
    {
    }

    public TrackPropertiesDialog(string title, string name, bool supportsInheritedColor)
    {
        InitializeComponent();
        _supportsInheritedColor = supportsInheritedColor;
        Title = title;
        TitleText.Text = title;
        NameTextBox.Text = name;
        UseInstrumentColorCheckBox.IsVisible = supportsInheritedColor;
        UseInstrumentColorCheckBox.IsChecked = supportsInheritedColor;
        UpdateColorPresentation();
    }

    public string TrackName { get; private set; } = string.Empty;
    public string? ExplicitColorHex { get; private set; }
    public bool Confirmed { get; private set; }

    private void OnUseInstrumentColorClick(object? sender, RoutedEventArgs e) =>
        UpdateColorPresentation();

    private void OnSelectColorClick(object? sender, RoutedEventArgs e)
    {
        _paletteIndex = (_paletteIndex + 1) % PlaceholderPalette.Length;
        _selectedColor = PlaceholderPalette[_paletteIndex];
        UpdateColorPresentation();
    }

    private void UpdateColorPresentation()
    {
        bool inherits = _supportsInheritedColor && UseInstrumentColorCheckBox.IsChecked == true;
        SelectColorButton.IsEnabled = !inherits;
        ColorSwatch.Fill = new SolidColorBrush(
            Color.FromRgb(_selectedColor.Red, _selectedColor.Green, _selectedColor.Blue));
        ColorText.Text = $"#{_selectedColor.Red:X2}{_selectedColor.Green:X2}{_selectedColor.Blue:X2}";
        ColorHintText.Text = inherits
            ? "The displayed color follows the bound Event Instrument."
            : "Select the color used for this Track and its Segments.";
        if (inherits)
        {
            ErrorTextBlock.Text = string.Empty;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorTextBlock.Text = "Track name must not be empty.";
            return;
        }

        bool inherits = _supportsInheritedColor && UseInstrumentColorCheckBox.IsChecked == true;
        TrackName = name;
        ExplicitColorHex = inherits
            ? null
            : $"#{_selectedColor.Red:X2}{_selectedColor.Green:X2}{_selectedColor.Blue:X2}";
        // Avalonia has no DialogResult: callers read Confirmed after ShowDialog completes.
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
