using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Domain;

namespace Midora.Desktop;

public partial class NewRawMidiTrackDialog : Window
{
    private readonly Dictionary<(int Port, int Channel), MidiChannelRoot> _fixedRoutes = [];

    public NewRawMidiTrackDialog(IEnumerable<MidiChannelRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        InitializeComponent();
        ExistingRoutes.Add(new(null!, "Create a new route"));
        foreach (MidiChannelRoot root in roots.Where(value => value.RoutingMode == MidiChannelRootRoutingMode.Fixed))
        {
            _fixedRoutes.Add(
                (root.FixedZeroBasedPort + 1, root.FixedZeroBasedChannel + 1),
                root);
            ExistingRoutes.Add(new(
                root,
                $"P.{root.FixedZeroBasedPort + 1} Ch.{root.FixedZeroBasedChannel + 1} {root.ChannelMode}"));
        }
        RoutingModeBox.ItemsSource = Enum.GetValues<MidiChannelRootRoutingMode>();
        ChannelModeBox.ItemsSource = Enum.GetValues<MidiChannelMode>();
        PortBox.ItemsSource = Enumerable.Range(1, 16);
        ChannelBox.ItemsSource = Enumerable.Range(1, 16);
        DataContext = this;
        ExistingRouteBox.SelectedIndex = 0;
        RoutingModeBox.SelectedItem = MidiChannelRootRoutingMode.Auto;
        ChannelModeBox.SelectedItem = MidiChannelMode.Melodic;
        PortBox.SelectedItem = 1;
        ChannelBox.SelectedItem = 1;
        UpdateControls();
    }

    public ObservableCollection<SelectionDialogItem> ExistingRoutes { get; } = [];
    public string TrackName { get; private set; } = string.Empty;
    public MidiChannelRootRoutingMode RoutingMode { get; private set; }
    public int OneBasedPort { get; private set; }
    public int OneBasedChannel { get; private set; }
    public MidiChannelMode ChannelMode { get; private set; }

    private void OnExistingRouteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (ExistingRouteBox.SelectedItem is SelectionDialogItem { Value: MidiChannelRoot root })
        {
            RoutingModeBox.SelectedItem = MidiChannelRootRoutingMode.Fixed;
            PortBox.SelectedItem = (int)root.FixedZeroBasedPort + 1;
            ChannelBox.SelectedItem = (int)root.FixedZeroBasedChannel + 1;
            ChannelModeBox.SelectedItem = root.ChannelMode;
        }
        UpdateControls();
    }

    private void OnRoutingModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void OnFixedCoordinateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) UpdateControls();
    }

    private void UpdateControls()
    {
        bool existing = ExistingRouteBox.SelectedItem is SelectionDialogItem { Value: MidiChannelRoot };
        bool fixedRoute = RoutingModeBox.SelectedItem is MidiChannelRootRoutingMode.Fixed;
        MidiChannelRoot? matched = fixedRoute
            && PortBox.SelectedItem is int port
            && ChannelBox.SelectedItem is int channel
            && _fixedRoutes.TryGetValue((port, channel), out MidiChannelRoot? existingAtAddress)
                ? existingAtAddress
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

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        string name = TrackNameBox.Text.Trim();
        if (RoutingModeBox.SelectedItem is not MidiChannelRootRoutingMode routingMode
            || PortBox.SelectedItem is not int port
            || ChannelBox.SelectedItem is not int channel
            || ChannelModeBox.SelectedItem is not MidiChannelMode channelMode)
        {
            ErrorText.Text = "Complete the MIDI route configuration.";
            return;
        }
        TrackName = name;
        RoutingMode = routingMode;
        OneBasedPort = port;
        OneBasedChannel = channel;
        ChannelMode = channelMode;
        DialogResult = true;
    }

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
