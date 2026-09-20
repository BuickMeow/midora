using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

public partial class OnionSettingsDialog : Window, INotifyPropertyChanged
{
    private const string HelpText =
        "ONION SKIN\n"
        + "The layer icon beside Zoom opens a menu. Enable/Disable keeps your choices. "
        + "Show Previous/Next displays only that neighbor, without changing your saved custom sources. "
        + "Select Tracks/SubVoices reopens those custom checks; OK switches back to custom sources and enables onion skin. "
        + "Cancel changes nothing. Settings controls opacity only. The current track/SubVoice is excluded. "
        + "All Segments on the same target track share this setting.\n\n"
        + "TIME AND LAYERS\n"
        + "Track notes line up at their absolute Arrangement positions; hidden source Segment content is clipped. "
        + "Sources follow the current track/SubVoice order, with later sources on top. "
        + "Your editable notes and selection remain above the ghosts.\n\n"
        + "ALL TRACKS\n"
        + "Open the layer icon beside Arrangement Zoom. Raw shows source notes. Compiled expands only Logical Tracks from the last successful compilation "
        + "(mapped pitches, loops and emitted gates); MIDI Tracks always show current source notes, not FIFO-paired lengths from the final MIDI stream. "
        + "Logical stale means that expansion is from an older successful result. Preparation is asynchronous and cancelable; Refresh retries the logical index. "
        + "MIDI notes remain visible even without a successful compilation. No notes or lanes can be edited here.\n\n"
        + "NAVIGATION\n"
        + "Middle-drag to pan. Wheel scrolls keys; Shift+wheel scrolls time. Ctrl+wheel zooms time over the piano roll, or key height over the keyboard ruler. "
        + "Left-click the All Tracks time ruler or note area to set the playback cursor (seek while playing). This does not edit notes or select a time range. "
        + "All Tracks obeys Follow Playback: middle-dragging suspends following until release.\n\n"
        + "SAVING\n"
        + "Opacity, enable state, custom sources and the current Custom/Previous/Next mode are saved together. Neighbor modes follow the current formal order without wrapping. "
        + "These settings do not create Undo entries or a Project modified indicator. Use Save or Save Copy explicitly to retain them. "
        + "Closing without saving may lose view changes without a prompt. Deleting a custom source hides its ghosts; Undo restores them. Duplicate copies the target's settings.";

    private double _opacityPercent;

    public OnionSettingsDialog()
        : this(0.35)
    {
    }

    public OnionSettingsDialog(double opacity)
    {
        OpacityPercent = opacity * 100;
        InitializeComponent();
        DataContext = this;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    public double OpacityPercent
    {
        get => _opacityPercent;
        set
        {
            if (!double.IsFinite(value)) return;
            double clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _opacityPercent) < double.Epsilon) return;
            _opacityPercent = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpacityPercent)));
        }
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => ShowHelp(this);

    internal static void ShowHelp(Window? owner)
    {
        var content = new TextBlock
        {
            Text = HelpText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18)
        };
        var window = new Window
        {
            Title = "Onion Skin and All Tracks",
            Width = 620,
            Height = 480,
            MinWidth = 420,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = ResolveBrush(owner, "Brush.Surface.0"),
            Foreground = ResolveBrush(owner, "Brush.Text.Primary"),
            Content = new ScrollViewer { Content = content }
        };
        if (owner is null) window.Show();
        else _ = window.ShowDialog(owner);
    }

    private static IBrush? ResolveBrush(Window? owner, string key)
    {
        return owner is not null
               && owner.TryFindResource(key, out object? value)
               && value is IBrush brush
            ? brush
            : null;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        // WPF set DialogResult = true.
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        // WPF set DialogResult = false.
        Close();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
