using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Midora.Avalonia.Windows;

/// <summary>
/// Mirrors the WPF Port mapping review: every source Port that falls outside Midora's 1-16 range is
/// mapped to one unused Midora Port. The same Port number is preselected when it is available, and
/// the mapping is a strict one-to-one assignment.
/// </summary>
public partial class MidiPortMappingDialog : Window
{
    private readonly Dictionary<byte, (ComboBox Selector, List<byte> Candidates)> _selectors = [];

    public MidiPortMappingDialog()
        : this([32])
    {
    }

    private MidiPortMappingDialog(IReadOnlyList<byte> sourcePorts)
    {
        InitializeComponent();
        Title = "Review MIDI Port Mapping";
        List<byte> ordered = [.. sourcePorts.Distinct().Order()];
        HashSet<byte> used = [];
        for (int index = 0; index < ordered.Count; index++)
        {
            byte sourcePort = ordered[index];
            MappingGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBlock label = new()
            {
                Text = $"Source Port {sourcePort + 1}",
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, index);
            Grid.SetColumn(label, 0);
            MappingGrid.Children.Add(label);

            List<byte> choices = [];
            if (sourcePort < 16)
            {
                choices.Add(sourcePort);
            }

            for (byte candidate = 0; candidate < 16; candidate++)
            {
                if (candidate != sourcePort)
                {
                    choices.Add(candidate);
                }
            }

            List<byte> available = [.. choices.Where(candidate => !used.Contains(candidate))];
            ComboBox selector = new()
            {
                ItemsSource = available.Select(candidate => $"Midora Port {candidate + 1}").ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0)
            };
            _selectors[sourcePort] = (selector, available);
            used.Add(available[0]);
            Grid.SetRow(selector, index);
            Grid.SetColumn(selector, 2);
            MappingGrid.Children.Add(selector);
        }
    }

    /// <summary>Shows the dialog and returns the one-to-one mapping, or null when cancelled.</summary>
    public static async Task<IReadOnlyDictionary<byte, byte>?> ShowAsync(
        Window? owner,
        IReadOnlyList<byte> sourcePorts)
    {
        MidiPortMappingDialog dialog = new(sourcePorts);
        bool confirmed = owner is { IsVisible: true }
            ? await dialog.ShowDialog<bool>(owner)
            : false;
        return confirmed ? dialog.BuildMapping() : null;
    }

    private IReadOnlyDictionary<byte, byte> BuildMapping()
    {
        Dictionary<byte, byte> mapping = [];
        foreach (KeyValuePair<byte, (ComboBox Selector, List<byte> Candidates)> entry in _selectors)
        {
            int selected = entry.Value.Selector.SelectedIndex;
            if (selected < 0 || selected >= entry.Value.Candidates.Count)
            {
                continue;
            }

            mapping[entry.Key] = entry.Value.Candidates[selected];
        }

        return mapping;
    }

    private void OnImportClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
