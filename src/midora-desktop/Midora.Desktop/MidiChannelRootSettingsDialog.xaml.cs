using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MidiChannelRootSettingsDialog : Window
{
    public MidiChannelRootSettingsDialog(MidiChannelRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);
        InitializeComponent();
        RoutingModeBox.ItemsSource = Enum.GetValues<MidiChannelRootRoutingMode>();
        ChannelModeBox.ItemsSource = Enum.GetValues<MidiChannelMode>();
        PortBox.ItemsSource = Enumerable.Range(1, 16);
        ChannelBox.ItemsSource = Enumerable.Range(1, 16);
        RoutingModeBox.SelectedItem = root.RoutingMode;
        PortBox.SelectedItem = checked((int)root.FixedZeroBasedPort + 1);
        ChannelBox.SelectedItem = checked((int)root.FixedZeroBasedChannel + 1);
        ChannelModeBox.SelectedItem = root.ChannelMode;
        UpdateRouteControls();
    }

    public MidiChannelRootRoutingMode RoutingMode { get; private set; }
    public int OneBasedPort { get; private set; }
    public int OneBasedChannel { get; private set; }
    public MidiChannelMode ChannelMode { get; private set; }

    private void OnRoutingModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateRouteControls();
    }

    private void UpdateRouteControls()
    {
        bool fixedRoute = RoutingModeBox.SelectedItem is MidiChannelRootRoutingMode.Fixed;
        FixedRoutePanel.IsEnabled = fixedRoute;
        FixedRoutePanel.Opacity = fixedRoute ? 1 : 0.55;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RoutingModeBox.SelectedItem is not MidiChannelRootRoutingMode routingMode)
                throw new InvalidOperationException("Select a routing mode.");
            if (PortBox.SelectedItem is not int port)
                throw new InvalidOperationException("Select a MIDI Port.");
            if (ChannelBox.SelectedItem is not int channel)
                throw new InvalidOperationException("Select a MIDI Channel.");
            if (ChannelModeBox.SelectedItem is not MidiChannelMode channelMode)
                throw new InvalidOperationException("Select a channel mode.");
            RoutingMode = routingMode;
            OneBasedPort = port;
            OneBasedChannel = channel;
            ChannelMode = channelMode;
            DialogResult = true;
        }
        catch (InvalidOperationException exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
