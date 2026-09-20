using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Views;

public partial class ArrangementView : UserControl
{
    private readonly DemoTimelineSource _demo = DemoTimelineSource.Create();

    private ITimelineRenderItemSource _source;
    private IReadOnlyList<string> _trackNames;
    private Func<TimelineRenderItem, ITimelineSegmentPreviewSource?>? _previewProvider;
    private long _ticksPerQuarterNote = 480;

    /// <summary>Raised with a zero-based MIDI track index when a lane is double-clicked.</summary>
    public event EventHandler<int>? TrackActivated;

    /// <summary>Raised with (track index, segment start tick) when a Segment is double-clicked.</summary>
    public event EventHandler<(int TrackIndex, long StartTick)>? SegmentActivated;

    public ArrangementView()
    {
        InitializeComponent();

        _source = _demo;
        _trackNames = _demo.TrackNames;
        _previewProvider = segment => _demo.GetPreviewSource(segment);

        Timeline.LaneActivated += (_, lane) => TrackActivated?.Invoke(this, lane - 1);
        Timeline.SegmentActivated += (_, activation) =>
            SegmentActivated?.Invoke(this, (activation.Lane - 1, activation.StartTick));
        LaneHeaders.MuteToggled += (_, e) => Timeline.SetLaneMute(e.Lane, e.Active);
        LaneHeaders.SoloToggled += (_, e) => Timeline.SetLaneSolo(e.Lane, e.Active);
        Timeline.PointerTickChanged += (_, tick) => UpdateReadout(tick);
        Timeline.SelectionChanged += (_, item) =>
        {
            HintText.Text = item is { } selected
                ? $"Selected {selected.Kind} '{selected.Label}' · start {selected.StartTick} · end {selected.EndTick}"
                : "Selection cleared";
        };
        Timeline.PropertyChanged += (_, change) =>
        {
            if (change.Property == TimelineSurface.TickSpanProperty)
            {
                UpdateZoomText();
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
        UpdateZoomText();
        UpdateReadout(preserveView ? Timeline.StartTick : 0);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Max(1, (long)(Timeline.TickSpan / 1.25));

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Min(
            Math.Max(1, _source.MaximumEndTick) * 2,
            (long)(Timeline.TickSpan * 1.25));

    private void UpdateZoomText()
    {
        long percent = Math.Max(1, _source.MaximumEndTick) * 100 / Math.Max(1, Timeline.TickSpan);
        ZoomText.Text = $"{percent}%";
    }

    private void UpdateReadout(long tick)
    {
        long ticksPerBar = _ticksPerQuarterNote * 4;
        long bar = tick / ticksPerBar + 1;
        long beat = tick % ticksPerBar / _ticksPerQuarterNote + 1;
        TickText.Text = $"Bar {bar}.{beat}.{tick % _ticksPerQuarterNote:000}";
        TickChip.Text = $"({tick})";
        HintText.Text = $"Tick {tick} · Bar {bar} · Wheel: pan · Ctrl+wheel: zoom · Drag: select";
    }
}
