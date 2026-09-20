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

/// <summary>
/// Avalonia port of <c>Midora.Desktop.SelectionDialog</c>. Options, prompt, preselect-by-value,
/// double-click accept and the single-item wheel option are ported; no Midora view models are used.
/// </summary>
public partial class SelectionDialog : Window
{
    private readonly bool _useSingleItemWheel;
    private bool _closing;

    public SelectionDialog()
        : this(
            "Select an Option",
            "Choose how Midora should treat the selected material.",
            [
                new Item(0, "Keep as Logical Track", "Preserve instruments, mappings and lifecycle."),
                new Item(1, "Convert to Pure MIDI", "Move the notes to a MIDI Channel Root."),
                new Item(2, "Leave unchanged", "Close this dialog without changing the selection.")
            ])
    {
    }

    public SelectionDialog(
        string title,
        string prompt,
        IEnumerable<Item> options,
        object? selectedValue = null,
        bool useSingleItemWheel = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(options);

        InitializeComponent();
        _useSingleItemWheel = useSingleItemWheel;
        Title = title;
        Prompt = prompt ?? string.Empty;
        foreach (Item option in options) Options.Add(option);
        DataContext = this;
        OptionsList.ItemsSource = Options;

        if (Options.Count != 0)
        {
            int selectedIndex = selectedValue is null
                ? -1
                : Options.ToList().FindIndex(option => Equals(option.Value, selectedValue));
            OptionsList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            OptionsList.ScrollIntoView(OptionsList.SelectedIndex);
        }
    }

    public sealed record Item(object? Value, string Display, string Description = "");

    public string Prompt { get; }

    public ObservableCollection<Item> Options { get; } = [];

    public object? SelectedValue { get; private set; }

    public bool Confirmed { get; private set; }

    private void OnSelectClick(object? sender, RoutedEventArgs e)
    {
        if (OptionsList.SelectedItem is not Item selected) return;
        SelectedValue = selected.Value;
        // WPF set DialogResult = true; Avalonia closes and the caller reads Confirmed.
        Confirmed = true;
        _closing = true;
        Close();
    }

    private void OnOptionsDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        OnSelectClick(sender, e);
    }

    // WPF optionally installed a one-item-per-notch wheel router; Avalonia needs the ScrollViewer lookup.
    private void OnOptionsPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_useSingleItemWheel || e.Delta.Y == 0) return;
        ScrollViewer? scroll = OptionsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        double step = OptionsList.ContainerFromIndex(0)?.Bounds.Height ?? 0;
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
        SelectedValue = null;
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
