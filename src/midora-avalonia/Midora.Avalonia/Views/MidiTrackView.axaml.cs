using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Midora.Avalonia.Import;
using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Views;

/// <summary>
/// Slice E track editor: one imported SMF track shown through the Avalonia
/// <see cref="TimelineSurface"/> in Notes (piano roll), Velocity, Events, or Conductor mode.
/// The view is a thin host; rendering, hit-testing, panning, zooming, and selection all live
/// in the presentation surface. Until <see cref="SetProject"/> is called the surface has no
/// source and the footer shows the empty hint.
/// </summary>
public partial class MidiTrackView : UserControl
{
    private const long DefaultTicksPerQuarterNote = 480;
    private const int DefaultFirstPitch = 36;
    private const int DefaultPitchCount = 60;
    private const double DefaultValueMinimum = 0d;
    private const double DefaultValueMaximum = 127d;
    private const double NotesLaneHeight = 12d;
    private const double ConductorLaneHeight = 20d;
    private const double HorizontalZoomStep = 1.25;
    private const string TimelineHint =
        "Wheel: pan · Ctrl+wheel: zoom · Drag: select · Middle-drag: pan";

    private MidiTimelineSource? _source;
    private int _trackIndex;
    private long _ticksPerQuarterNote = DefaultTicksPerQuarterNote;
    private TimelineSurfaceMode _mode = TimelineSurfaceMode.PianoRoll;
    private bool _suppressTrackSelection;
    private string? _selectionText;
    private string? _laneText;

    public MidiTrackView()
    {
        InitializeComponent();

        Timeline.PointerTickChanged += OnPointerTickChanged;
        Timeline.SelectionChanged += OnSelectionChanged;
        Timeline.LaneActivated += OnLaneActivated;

        SetToolbarEnabled(false);
        SetMode(TimelineSurfaceMode.PianoRoll);
        UpdateTickReadout(0);
        RefreshFooter();
    }

    /// <summary>
    /// Binds an imported MIDI project to the editor. The combo lists every non-conductor
    /// project track; <paramref name="trackIndex"/> is the zero-based project track index.
    /// </summary>
    public void SetProject(MidiTimelineSource source, int trackIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _ticksPerQuarterNote = Math.Max(1, source.Project.TicksPerQuarterNote);
        PopulateTracks(source, trackIndex);
        SetToolbarEnabled(true);
        ApplyTrack();
    }

    private void PopulateTracks(MidiTimelineSource source, int requestedIndex)
    {
        _suppressTrackSelection = true;
        try
        {
            TrackCombo.Items.Clear();
            for (int index = 0; index < source.TrackCount; index++)
            {
                int nameIndex = index + 1;
                string display = nameIndex < source.TrackNames.Count
                    ? source.TrackNames[nameIndex]
                    : "Track " + (index + 1).ToString(CultureInfo.InvariantCulture);
                TrackCombo.Items.Add(new ComboBoxItem
                {
                    Content = display,
                    Tag = index
                });
            }

            _trackIndex = source.TrackCount == 0
                ? 0
                : Math.Clamp(requestedIndex, 0, source.TrackCount - 1);
            TrackCombo.SelectedIndex = source.TrackCount == 0 ? -1 : _trackIndex;
        }
        finally
        {
            _suppressTrackSelection = false;
        }
    }

    private void ApplyTrack()
    {
        if (_source is null)
        {
            Timeline.Source = null;
            RefreshFooter();
            return;
        }

        if (_source.TrackCount == 0)
        {
            Timeline.Source = null;
            _selectionText = null;
            _laneText = null;
            FooterText.Text = "No MIDI tracks in this project.";
            return;
        }

        _trackIndex = Math.Clamp(_trackIndex, 0, _source.TrackCount - 1);
        _selectionText = null;
        _laneText = null;

        Timeline.Source = _source.CreateTrackSource(_trackIndex);
        Timeline.PreviewProvider = null;
        Timeline.TicksPerQuarterNote = _ticksPerQuarterNote;
        Timeline.TrackNames = static lane =>
            lane < 256 ? "Ch " + lane.ToString(CultureInfo.InvariantCulture) : null;
        Timeline.FirstPitch = DefaultFirstPitch;
        Timeline.PitchCount = DefaultPitchCount;
        Timeline.ValueMinimum = DefaultValueMinimum;
        Timeline.ValueMaximum = DefaultValueMaximum;
        Timeline.TickSpan = Math.Max(1, _ticksPerQuarterNote * 4L);
        Timeline.StartTick = 0;
        Timeline.SelectedId = null;

        SetMode(DefaultModeForTrack(_trackIndex));
        UpdateTickReadout(0);
        RefreshFooter();
    }

    /// <summary>
    /// The default surface mode for a track: notes when the track has notes, otherwise the
    /// event lane for event-only tracks; empty tracks keep the piano roll default.
    /// </summary>
    private TimelineSurfaceMode DefaultModeForTrack(int trackIndex)
    {
        if (_source is null || trackIndex < 0 || trackIndex >= _source.TrackCount)
        {
            return TimelineSurfaceMode.PianoRoll;
        }

        ImportedMidiTrack track = _source.Project.Tracks[trackIndex];
        if (track.Notes.Count != 0)
        {
            return TimelineSurfaceMode.PianoRoll;
        }

        return track.Events.Count != 0
            ? TimelineSurfaceMode.EventLanes
            : TimelineSurfaceMode.PianoRoll;
    }

    private void SetMode(TimelineSurfaceMode mode)
    {
        _mode = mode;
        NotesButton.IsChecked = mode == TimelineSurfaceMode.PianoRoll;
        VelocityButton.IsChecked = mode == TimelineSurfaceMode.Velocity;
        EventsButton.IsChecked = mode == TimelineSurfaceMode.EventLanes;
        ConductorButton.IsChecked = mode == TimelineSurfaceMode.Conductor;
        ApplyMode();
    }

    private void ApplyMode()
    {
        Timeline.SurfaceMode = _mode;
        Timeline.LaneHeight = _mode == TimelineSurfaceMode.Conductor
            ? ConductorLaneHeight
            : NotesLaneHeight;
    }

    private void SetToolbarEnabled(bool enabled)
    {
        TrackCombo.IsEnabled = enabled;
        NotesButton.IsEnabled = enabled;
        VelocityButton.IsEnabled = enabled;
        EventsButton.IsEnabled = enabled;
        ConductorButton.IsEnabled = enabled;
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
    }

    private void OnTrackSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrackSelection || _source is null)
        {
            return;
        }

        if (TrackCombo.SelectedItem is ComboBoxItem { Tag: int index })
        {
            _trackIndex = index;
            ApplyTrack();
        }
    }

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse(tag, out TimelineSurfaceMode mode))
        {
            return;
        }

        SetMode(mode);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Max(1, (long)(Timeline.TickSpan / HorizontalZoomStep));

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        long maximum = Math.Max(1, _source?.MaximumEndTick ?? Timeline.TickSpan);
        long doubled = maximum > long.MaxValue / 2 ? long.MaxValue : maximum * 2;
        long cap = Math.Max(Math.Max(1, _ticksPerQuarterNote * 4L), doubled);
        Timeline.TickSpan = Math.Min(cap, (long)(Timeline.TickSpan * HorizontalZoomStep));
    }

    private void OnPointerTickChanged(object? sender, long tick) => UpdateTickReadout(tick);

    private void OnSelectionChanged(object? sender, TimelineRenderItem? item)
    {
        _selectionText = item is { } selected
            ? "Selected " + selected.Kind
              + " #" + selected.Id.Value.ToString(CultureInfo.InvariantCulture)
              + " · start " + selected.StartTick.ToString(CultureInfo.InvariantCulture)
              + " · end " + selected.EndTick.ToString(CultureInfo.InvariantCulture)
              + " · value " + selected.Value.ToString("0.##", CultureInfo.InvariantCulture)
            : null;
        RefreshFooter();
    }

    private void OnLaneActivated(object? sender, int lane)
    {
        string? name = Timeline.TrackNames?.Invoke(lane);
        _laneText = name is { Length: > 0 }
            ? "Lane " + lane.ToString(CultureInfo.InvariantCulture) + " · " + name
            : "Lane " + lane.ToString(CultureInfo.InvariantCulture);
        RefreshFooter();
    }

    private void UpdateTickReadout(long tick)
    {
        long ticksPerQuarterNote = Math.Max(1, _ticksPerQuarterNote);
        long ticksPerBar = ticksPerQuarterNote * 4;
        long bar = tick / ticksPerBar + 1;
        long beat = tick % ticksPerBar / ticksPerQuarterNote + 1;
        TickText.Text = "Bar " + bar.ToString(CultureInfo.InvariantCulture)
            + "." + beat.ToString(CultureInfo.InvariantCulture)
            + "." + (tick % ticksPerQuarterNote).ToString("000", CultureInfo.InvariantCulture);
    }

    private void RefreshFooter()
    {
        if (_source is null)
        {
            FooterText.Text = "No MIDI project loaded.";
            return;
        }

        if (_selectionText is { } selection)
        {
            FooterText.Text = selection;
            return;
        }

        FooterText.Text = _laneText ?? TimelineHint;
    }
}
