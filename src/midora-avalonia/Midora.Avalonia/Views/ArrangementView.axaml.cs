using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Views;

public partial class ArrangementView : UserControl
{
    private static readonly string[] SubdivisionPresets =
    [
        "Bar",
        "1/1",
        "1/2",
        "1/3",
        "1/4",
        "1/6",
        "3/16",
        "1/8",
        "1/12",
        "3/32",
        "1/16",
        "1/24",
        "3/64",
        "1/32",
        "1/48",
        "1/64",
        "1/128",
        "1/256",
    ];

    private readonly DemoTimelineSource _demo = DemoTimelineSource.Create();

    private ITimelineRenderItemSource _source;
    private IReadOnlyList<string> _trackNames;
    private Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>? _previewProvider;
    private long _ticksPerQuarterNote = 480;

    /// <summary>Raised with a zero-based MIDI track index when a lane is double-clicked.</summary>
    public event EventHandler<int>? TrackActivated;

    /// <summary>Raised with (track index, segment start tick) when a Segment is double-clicked.</summary>
    public event EventHandler<(int TrackIndex, long StartTick)>? SegmentActivated;

    /// <summary>Raised when the All Tracks workspace should be opened.</summary>
    public event EventHandler? AllTracksRequested;

    /// <summary>Raised with "Logical", "Instrument", or "Midi" from the Add Track corner menu.</summary>
    public event EventHandler<string>? CreateTrackRequested;

    private const double MinimumLaneHeight = 28d;
    private const double MaximumLaneHeight = 112d;

    public ArrangementView()
    {
        InitializeComponent();

        _source = _demo;
        _trackNames = _demo.TrackNames;
        _previewProvider = segment => _demo.GetPreviewSource(segment);

        SubdivisionBox.ItemsSource = SubdivisionPresets;
        SubdivisionBox.Text = "1/8";

        Timeline.LaneActivated += (_, lane) => TrackActivated?.Invoke(this, lane - 1);
        Timeline.SegmentActivated += (_, activation) =>
            SegmentActivated?.Invoke(this, (activation.Lane - 1, activation.StartTick));
        LaneHeaders.MuteToggled += (_, e) => Timeline.SetLaneMute(e.Lane, e.Active);
        LaneHeaders.SoloToggled += (_, e) => Timeline.SetLaneSolo(e.Lane, e.Active);
        Timeline.PointerTickChanged += (_, tick) => ShowPointerTick(tick);
        Timeline.PropertyChanged += (_, change) =>
        {
            if (change.Property == TimelineSurface.FirstLaneProperty)
            {
                LaneHeaders.FirstLane = Timeline.FirstLane;
            }
        };

        ApplySource();
    }

    /// <summary>
    /// Replaces the arrangement content. The MIDI import path passes the real imported
    /// project; leaving this untouched keeps the deterministic demo content.
    /// </summary>
    public void SetSource(
        ITimelineRenderItemSource source,
        long ticksPerQuarterNote,
        IReadOnlyList<string> trackNames,
        Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>? previewProvider,
        bool preserveView = false)
    {
        _source = source;
        _ticksPerQuarterNote = Math.Max(1, ticksPerQuarterNote);
        _trackNames = trackNames;
        _previewProvider = previewProvider;
        ApplySource(preserveView);
    }

    public void UseDemoSource() =>
        SetSource(_demo, 480, _demo.TrackNames, segment => _demo.GetPreviewSource(segment));

    public void SetPlaybackTick(long tick) => Timeline.PlaybackTick = tick;

    private void ApplySource(bool preserveView = false)
    {
        long maximum = Math.Max(1, _source.MaximumEndTick);
        LaneHeaders.LaneHeight = Timeline.LaneHeight;
        LaneHeaders.TrackNames = _trackNames;
        Timeline.TicksPerQuarterNote = _ticksPerQuarterNote;
        Timeline.PreviewProvider = _previewProvider;
        Timeline.TrackNames = lane =>
            lane >= 0 && lane < _trackNames.Count ? _trackNames[lane] : null;
        if (!preserveView)
        {
            Timeline.StartTick = 0;
            Timeline.SelectedId = null;
            Timeline.TickSpan = Math.Clamp(_ticksPerQuarterNote * 64L, 1, maximum);
        }

        Timeline.Source = _source;
        ContextText.Text = $"{Math.Max(0, _trackNames.Count - 1)} tracks · {CountSegments()} segments";
    }

    private int CountSegments()
    {
        List<TimelineRenderItem> scratch = [];
        _source.QueryInto(0, long.MaxValue / 2, 0, int.MaxValue / 2, scratch);
        return scratch.Count(item => item.Kind == TimelineItemKind.Segment);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Max(1, (long)(Timeline.TickSpan / 1.25));

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Min(
            Math.Max(1, _source.MaximumEndTick) * 2,
            (long)(Timeline.TickSpan * 1.25));

    private void OnAllTracksClick(object? sender, RoutedEventArgs e) =>
        AllTracksRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddTrackMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
        {
            CreateTrackRequested?.Invoke(this, tag);
        }
    }

    private void OnLaneZoomInClick(object? sender, RoutedEventArgs e) =>
        SetLaneHeight(Math.Min(MaximumLaneHeight, Timeline.LaneHeight * 1.25));

    private void OnLaneZoomOutClick(object? sender, RoutedEventArgs e) =>
        SetLaneHeight(Math.Max(MinimumLaneHeight, Timeline.LaneHeight / 1.25));

    private void SetLaneHeight(double laneHeight)
    {
        Timeline.LaneHeight = Math.Clamp(laneHeight, MinimumLaneHeight, MaximumLaneHeight);
        LaneHeaders.LaneHeight = Timeline.LaneHeight;
    }

    private void OnResetMonitoringClick(object? sender, RoutedEventArgs e)
    {
        Timeline.ClearLaneStates();
        LaneHeaders.ClearStates();
    }

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } clicked ||
            !Enum.TryParse(tag, out TimelineToolMode tool))
        {
            return;
        }

        Timeline.ToolMode = tool;
        DrawToggle.IsChecked = tool == TimelineToolMode.Draw;
        SelectToggle.IsChecked = tool == TimelineToolMode.Select;
        SplitToggle.IsChecked = tool == TimelineToolMode.Split;
        EraseToggle.IsChecked = tool == TimelineToolMode.Erase;
        clicked.IsChecked = true;
    }

    private void ShowPointerTick(long tick)
    {
        bool visible = tick >= 0;
        PointerText.IsVisible = visible;
        PointerSeparator.IsVisible = visible;
        if (visible)
        {
            PointerText.Text = tick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            PointerText.Text = string.Empty;
        }
    }
}
