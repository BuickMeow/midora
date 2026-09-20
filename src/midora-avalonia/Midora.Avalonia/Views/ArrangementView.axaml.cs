using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Views;

public partial class ArrangementView : UserControl
{
    private readonly DemoTimelineSource _source = DemoTimelineSource.Create();

    public ArrangementView()
    {
        InitializeComponent();

        Timeline.Source = _source;
        Timeline.PreviewProvider = segment => _source.GetPreviewSource(segment);
        Timeline.TrackNames = lane =>
            lane >= 0 && lane < _source.TrackNames.Count ? _source.TrackNames[lane] : null;
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

        UpdateZoomText();
        UpdateReadout(0);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Max(1, (long)(Timeline.TickSpan / 1.25));

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) =>
        Timeline.TickSpan = Math.Min(_source.MaximumEndTick * 2, (long)(Timeline.TickSpan * 1.25));

    private void UpdateZoomText()
    {
        long percent = _source.MaximumEndTick * 100 / Math.Max(1, Timeline.TickSpan);
        ZoomText.Text = $"{percent}%";
    }

    private void UpdateReadout(long tick)
    {
        long ticksPerBar = _source.TicksPerQuarterNote * 4;
        long bar = tick / ticksPerBar + 1;
        long beat = tick % ticksPerBar / _source.TicksPerQuarterNote + 1;
        TickText.Text = $"Bar {bar}.{beat}.{tick % _source.TicksPerQuarterNote:000}";
        HintText.Text = $"Tick {tick} · Bar {bar} · Wheel: pan · Ctrl+wheel: zoom · Drag: select";
    }
}
