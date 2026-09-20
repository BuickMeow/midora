using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class MidiChannelRootSettingsDialog : Window
{
    private static readonly string[] RoutingModes = ["Auto", "Fixed"];
    private static readonly string[] ChannelModes = ["Melodic", "Percussion"];
    private bool _updating;

    public MidiChannelRootSettingsDialog()
        : this("Fixed", 1, 1, "Melodic")
    {
    }

    public MidiChannelRootSettingsDialog(
        string routingMode,
        int oneBasedPort,
        int oneBasedChannel,
        string channelMode)
    {
        InitializeComponent();
        _updating = true;
        RoutingModeBox.ItemsSource = RoutingModes;
        ChannelModeBox.ItemsSource = ChannelModes;
        PortBox.ItemsSource = Enumerable.Range(1, 16).ToArray();
        ChannelBox.ItemsSource = Enumerable.Range(1, 16).ToArray();
        RoutingModeBox.SelectedItem = RoutingModes.Contains(routingMode) ? routingMode : "Fixed";
        PortBox.SelectedItem = Math.Clamp(oneBasedPort, 1, 16);
        ChannelBox.SelectedItem = Math.Clamp(oneBasedChannel, 1, 16);
        ChannelModeBox.SelectedItem = ChannelModes.Contains(channelMode) ? channelMode : "Melodic";
        UpdateRouteControls();
        _updating = false;
    }

    public string RoutingMode { get; private set; } = "Fixed";
    public int OneBasedPort { get; private set; } = 1;
    public int OneBasedChannel { get; private set; } = 1;
    public string ChannelMode { get; private set; } = "Melodic";
    public bool Applied { get; private set; }

    private void OnRoutingModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateRouteControls();
        }
    }

    private void UpdateRouteControls()
    {
        bool fixedRoute = RoutingModeBox.SelectedItem is "Fixed";
        FixedRoutePanel.IsEnabled = fixedRoute;
        FixedRoutePanel.Opacity = fixedRoute ? 1 : 0.55;
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (RoutingModeBox.SelectedItem is not string routingMode)
        {
            ErrorText.Text = "Select a routing mode.";
            return;
        }

        if (PortBox.SelectedItem is not int port)
        {
            ErrorText.Text = "Select a MIDI Port.";
            return;
        }

        if (ChannelBox.SelectedItem is not int channel)
        {
            ErrorText.Text = "Select a MIDI Channel.";
            return;
        }

        if (ChannelModeBox.SelectedItem is not string channelMode)
        {
            ErrorText.Text = "Select a channel mode.";
            return;
        }

        RoutingMode = routingMode;
        OneBasedPort = port;
        OneBasedChannel = channel;
        ChannelMode = channelMode;
        // Avalonia has no DialogResult: callers read Applied after ShowDialog completes.
        Applied = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Applied = false;
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
