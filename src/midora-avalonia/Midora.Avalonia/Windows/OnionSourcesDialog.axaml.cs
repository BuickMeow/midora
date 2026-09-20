using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Midora.Avalonia.Windows;

public sealed class OnionSourceItem : INotifyPropertyChanged
{
    private bool _included;

    public OnionSourceItem(string id, string name, uint color, bool included)
    {
        Id = id;
        Name = name;
        ColorValue = color;
        _included = included;
        Color = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(
            (byte)(color >> 16),
            (byte)(color >> 8),
            (byte)color));
    }

    public string Id { get; }

    public string Name { get; }

    public uint ColorValue { get; }

    public IBrush Color { get; }

    public bool Included
    {
        get => _included;
        set
        {
            if (_included == value) return;
            _included = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class OnionSourcesDialog : Window
{
    private static readonly OnionSourceItem[] DemoSources =
    [
        new("onion.source.1", "Strings — Long", 0xE5484D, true),
        new("onion.source.2", "Strings — Short", 0xE8B34B, true),
        new("onion.source.3", "Brass", 0x62A6F6, false),
        new("onion.source.4", "Woodwinds", 0x58C487, true),
        new("onion.source.5", "Percussion", 0xC78AFF, false),
        new("onion.source.6", "Synth Lead", 0x91A6B8, true)
    ];

    public OnionSourcesDialog()
        : this(DemoSources, "Onion Skin Sources")
    {
    }

    public OnionSourcesDialog(IEnumerable<OnionSourceItem> sources, string title)
    {
        // Own the draft: neither cancellation nor checkbox edits mutate a preset.
        Sources = sources
            .Select(source => new OnionSourceItem(source.Id, source.Name, source.ColorValue, source.Included))
            .ToArray();
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        DataContext = this;
    }

    public IReadOnlyList<OnionSourceItem> Sources { get; }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (OnionSourceItem source in Sources) source.Included = true;
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        foreach (OnionSourceItem source in Sources) source.Included = false;
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => OnionSettingsDialog.ShowHelp(this);

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
