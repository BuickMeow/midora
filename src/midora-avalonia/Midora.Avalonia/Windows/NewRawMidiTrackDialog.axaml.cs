using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Midora.Avalonia.Windows;

public partial class NewRawMidiTrackDialog : Window
{
    private readonly List<ExistingRouteOption> _routes = [];
    private bool _updating;

    public NewRawMidiTrackDialog()
    {
        InitializeComponent();
        _updating = true;
        _routes.Add(new ExistingRouteOption("Create a new route", null, null, null));
        _routes.Add(new ExistingRouteOption("P.1 Ch.1 Melodic", 1, 1, "Melodic"));
        _routes.Add(new ExistingRouteOption("P.2 Ch.10 Percussion", 2, 10, "Percussion"));
        _routes.Add(new ExistingRouteOption("P.7 Ch.4 Melodic", 7, 4, "Melodic"));
        ExistingRouteBox.ItemsSource = _routes;
        RoutingModeBox.ItemsSource = new[] { "Auto", "Fixed" };
        ChannelModeBox.ItemsSource = new[] { "Melodic", "Percussion" };
        PortBox.ItemsSource = Enumerable.Range(1, 16).ToArray();
        ChannelBox.ItemsSource = Enumerable.Range(1, 16).ToArray();
        ExistingRouteBox.SelectedIndex = 0;
        RoutingModeBox.SelectedItem = "Auto";
        ChannelModeBox.SelectedItem = "Melodic";
        PortBox.SelectedItem = 1;
        ChannelBox.SelectedItem = 1;
        UpdateControls();
        _updating = false;
    }

    public string TrackName { get; private set; } = string.Empty;
    public string RoutingMode { get; private set; } = "Auto";
    public int OneBasedPort { get; private set; } = 1;
    public int OneBasedChannel { get; private set; } = 1;
    public string ChannelMode { get; private set; } = "Melodic";
    public bool Confirmed { get; private set; }

    private void OnExistingRouteChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        if (ExistingRouteBox.SelectedItem is ExistingRouteOption { Port: not null } root)
        {
            RoutingModeBox.SelectedItem = "Fixed";
            PortBox.SelectedItem = root.Port;
            ChannelBox.SelectedItem = root.Channel;
            ChannelModeBox.SelectedItem = root.ChannelMode;
        }

        UpdateControls();
    }

    private void OnRoutingModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateControls();
        }
    }

    private void OnFixedCoordinateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updating)
        {
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        bool existing = ExistingRouteBox.SelectedItem is ExistingRouteOption { Port: not null };
        bool fixedRoute = RoutingModeBox.SelectedItem is "Fixed";
        ExistingRouteOption? matched = fixedRoute
            && PortBox.SelectedItem is int port
            && ChannelBox.SelectedItem is int channel
                ? _routes.FirstOrDefault(value => value.Port == port && value.Channel == channel)
                : null;
        if (matched is not null)
        {
            ChannelModeBox.SelectedItem = matched.ChannelMode;
        }

        RoutingModeBox.IsEnabled = !existing;
        FixedRoutePanel.IsEnabled = fixedRoute && !existing;
        ChannelModeBox.IsEnabled = !existing && matched is null;
        FixedRoutePanel.Opacity = fixedRoute ? 1 : 0.55;
        MatchedRouteText.Text = matched is null
            ? string.Empty
            : $"This Port.Channel already exists. The new Track will join its {matched.ChannelMode} shared state.";
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (RoutingModeBox.SelectedItem is not string routingMode
            || PortBox.SelectedItem is not int port
            || ChannelBox.SelectedItem is not int channel
            || ChannelModeBox.SelectedItem is not string channelMode)
        {
            ErrorText.Text = "Complete the MIDI route configuration.";
            return;
        }

        TrackName = (TrackNameBox.Text ?? string.Empty).Trim();
        RoutingMode = routingMode;
        OneBasedPort = port;
        OneBasedChannel = channel;
        ChannelMode = channelMode;
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

    private sealed record ExistingRouteOption(string Display, int? Port, int? Channel, string? ChannelMode)
    {
        public override string ToString() => Display;
    }
}
