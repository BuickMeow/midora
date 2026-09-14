using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Typography;

namespace Midora.Desktop;

public partial class InstrumentChangeLane : UserControl
{
    private WorkspaceViewModel? _workspace;
    private MidoraId? _owner;
    private InstrumentChangeProjection? _index;
    private ProjectionRequest? _pending;
    private ProjectionRequest? _indexed;
    private CancellationTokenSource? _reading;
    private bool _running;
    private long? _selectTick;
    private InstrumentChangeValue? _selectedValue;
    private sealed record ProjectionRequest(InstrumentChangeSet Root, long Revision, MidoraId Owner,
        Func<InstrumentChange, InstrumentChangeValue?> Read, long Start, long End, int Pixels,
        long? SelectTick, MidoraId? SelectedId);
    private readonly InstrumentChangeSelectionState _selection = new();
    internal MidoraId? SelectedChangeId => _selection.SelectedId;
    internal MainWindow? Host => Window.GetWindow(this) as MainWindow;
    internal TimelineEditorSettings? Settings => DataContext switch
    {
        TimelineWorkspaceViewModel timeline => timeline.LaneEditorSettings,
        InstrumentWorkspaceViewModel voice => voice.EventLaneEditorSettings,
        _ => null
    };
    public InstrumentChangeLane()
    {
        InitializeComponent(); Points.Lane = this;
        Points.MouseLeftButtonUp += OnPointClick;
        Points.MouseRightButtonUp += OnContextClick;
        SizeChanged += (_, _) => { Backdrop.LaneHeight = Math.Max(20, Backdrop.ActualHeight - 24); Refresh(); };
        DataContextChanged += (_, _) => { Attach(); Refresh(); };
    }
    private void OnLoaded(object sender, RoutedEventArgs e) { Attach(); Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach(); Points.Clear(); _pending = null; _reading?.Cancel(); _pressPoint = null;
        if (!_running) { _index?.Dispose(); _index = null; _indexed = null; }
    }
    private void Attach()
    {
        Detach();
        if (!IsLoaded || DataContext is not WorkspaceViewModel workspace) return;
        _workspace = workspace; workspace.PropertyChanged += OnWorkspaceChanged;
        string start = workspace is InstrumentWorkspaceViewModel ? "TimelineStartTick" : "StartTick";
        string span = workspace is InstrumentWorkspaceViewModel ? "TimelineTickSpan" : "TickSpan";
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.StartTickProperty, new Binding(start));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.TickSpanProperty, new Binding(span));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.EditCursorTickProperty, new Binding("EditCursorTick"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.PlaybackCursorTickProperty, new Binding("PlaybackCursorTick"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.GridVisibleProperty, new Binding("EditorSettings.GridVisible"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.TimeSignatureMapProperty, new Binding("EditorSettings.TimeSignatureMap"));
        if (workspace is TimelineWorkspaceViewModel)
            Backdrop.SetBinding(Presentation.Controls.TimelineSurface.ProjectTickOffsetProperty, new Binding("ProjectTickOffset"));
        SnapButton.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(workspace is InstrumentWorkspaceViewModel ? "EventLaneEditorSettings.SnapEnabled" : "LaneEditorSettings.SnapEnabled") { Mode = BindingMode.TwoWay });
        Backdrop.Snapshot = new(0, "instrument-lane-background", [], laneLabels: ["Inst."]);
    }
    private void Detach()
    { if (_workspace is not null) _workspace.PropertyChanged -= OnWorkspaceChanged; _workspace = null; }
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Snapshot" or "SubVoiceSnapshot" or "SubVoiceEventSnapshot" or "ActiveSubVoiceId"
            or "StartTick" or "TickSpan" or "TimelineStartTick" or "TimelineTickSpan") Refresh();
        Points.InvalidateVisual();
    }
    internal void Refresh()
    {
        if (!IsLoaded) return;
        if (Host?.GetInstrumentLaneOwner(DataContext) is not { } context)
        { _pending = null; _reading?.Cancel(); Points.Clear(); _selection.Select(null); _selectedValue = null; return; }
        InstrumentChangeSet root; long generation; Func<InstrumentChange, InstrumentChangeValue?> read;
        if (context.Midi is { } midi)
        {
            root = midi.InstrumentChanges; generation = midi.ChannelEvents.Generation;
            var snapshot = midi.ChannelEvents.CreateQuerySnapshot();
            read = group => InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null;
            ChangeOwner(midi.Id);
        }
        else if (context.Voice is { } voice)
        {
            root = voice.InstrumentChanges; generation = voice.Events.Generation;
            var snapshot = voice.Events.CreateQuerySnapshot();
            read = group => InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null;
            ChangeOwner(voice.Id);
        }
        else return;
        _selection.Enter(_owner!.Value, root);
        var range = VisibleRange;
        _pending = new(root, generation, _owner!.Value, read, range.Start, range.End,
            (int)Math.Clamp(Points.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX, 1, 16_384),
            _selectTick, SelectedChangeId);
        _reading?.Cancel();
        if (!_running) _ = ReadProjectionAsync();
    }

    private async Task ReadProjectionAsync()
    {
        _running = true;
        try
        {
            while (_pending is { } request && IsLoaded)
            {
                _pending = null;
                using var cancel = new CancellationTokenSource(); _reading = cancel;
                try
                {
                    var result = await Task.Run(() =>
                    {
                        if (_index is null || _indexed is null || !ReferenceEquals(request.Root, _indexed.Root)
                            || request.Revision != _indexed.Revision || request.Owner != _indexed.Owner)
                        {
                            _index?.Dispose(); _index = null; _indexed = null;
                            _index = InstrumentChangeProjection.Create(request.Root, request.Read, cancel.Token);
                            _indexed = request;
                        }
                        var visible = _index.ReadVisible(request.Start, request.End, request.Pixels, cancel.Token);
                        InstrumentChangeValue? selected = request.SelectTick is { } tick
                            ? _index.ReadAtTick(tick, cancel.Token)
                            : request.SelectedId is { } id && request.Root.TryGet(id, out var group) ? request.Read(group) : null;
                        return (visible, selected);
                    });
                    if (!cancel.IsCancellationRequested && IsLoaded)
                    {
                        Points.SetValues(result.visible);
                        if (request.SelectTick is not null && _selectTick == request.SelectTick)
                        { Select(result.selected); _selectTick = null; }
                        else if (request.SelectedId == SelectedChangeId && _selectTick is null)
                            Select(result.selected);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (!cancel.IsCancellationRequested && IsLoaded)
                    { Points.Clear(); Host?.ReportInstrumentLaneFailure(exception); }
                }
                finally { _reading = null; }
            }
        }
        finally
        {
            _running = false;
            if (!IsLoaded) { _index?.Dispose(); _index = null; _indexed = null; }
        }
    }
    private void ChangeOwner(MidoraId owner)
    {
        if (_owner == owner) return;
        _owner = owner; _selectedValue = null; _selectTick = null;
        Points.Clear();
    }
    private void Select(InstrumentChangeValue? value)
    { _selectedValue = value; _selection.Select(value?.Id); Points.InvalidateVisual(); }
    internal double TickX(long tick) => 64 + (tick - Backdrop.StartTick) * ((Points.ActualWidth - 64) / Math.Max(1, (double)Backdrop.TickSpan));
    internal long PointTick(double x, bool snap = true)
    {
        double relative = (x - 64) * Backdrop.TickSpan / Math.Max(1, Points.ActualWidth - 64);
        long delta = relative >= long.MaxValue ? long.MaxValue : relative <= long.MinValue ? long.MinValue : (long)Math.Round(relative);
        long tick = delta > 0 && Backdrop.StartTick > long.MaxValue - delta ? long.MaxValue - 1
            : Math.Max(0, Backdrop.StartTick + delta);
        return Math.Min(long.MaxValue - 1, snap ? Settings?.SnapAbsolute(tick) ?? tick : tick);
    }
    internal (long Start, long End) VisibleRange
    {
        get
        {
            var (start, span) = DataContext switch
            {
                TimelineWorkspaceViewModel w => (w.StartTick, w.TickSpan),
                InstrumentWorkspaceViewModel w => (w.TimelineStartTick, w.TimelineTickSpan),
                _ => (Backdrop.StartTick, Backdrop.TickSpan)
            };
            return (start, start > long.MaxValue - span ? long.MaxValue : start + span);
        }
    }
    internal string Label(InstrumentChangeValue value) => Host?.InstrumentChangeLabel(value)
        ?? $"{value.BankMsb}.{value.BankLsb}.{value.Program}";
    internal bool HandleShortcut(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.A && Settings is { } settings)
        { settings.SnapEnabled = !settings.SnapEnabled; e.Handled = true; return true; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.P)
        { OpenSelected(); e.Handled = true; return true; }
        // Wrapper-specific bulk editing is the next accepted slice. Do not act on a hidden Note selection.
        if (e.Key == Key.Delete || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && e.Key is Key.C or Key.X or Key.V or Key.E or Key.Q or Key.T)
        { e.Handled = true; return true; }
        return false;
    }
    private int _clickCount;
    private Point? _pressPoint;
    private void OnPointClick(object sender, MouseButtonEventArgs e)
    {
        Focus(); e.Handled = true;
        Point point = e.GetPosition(Points);
        var press = _pressPoint; _pressPoint = null;
        if (press is null || Math.Abs(point.X - press.Value.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(point.Y - press.Value.Y) >= SystemParameters.MinimumVerticalDragDistance) return;
        if (point.X < 64 || point.Y < 24) return;
        var hit = Points.Hit(point);
        Select(hit);
        var mode = DataContext switch { TimelineWorkspaceViewModel w => w.ToolMode,
            InstrumentWorkspaceViewModel w => w.ToolMode, _ => TimelineToolMode.Select };
        if (hit is { } value && _clickCount >= 2) Host?.EditInstrumentChange(this, value.Tick, value.Id);
        else if (hit is null && mode == TimelineToolMode.Draw) Host?.EditInstrumentChange(this, PointTick(point.X), null);
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e); _clickCount = e.ClickCount;
        var point = e.GetPosition(Points);
        _pressPoint = point.X >= 64 && point.Y >= 24 && point.Y <= Points.ActualHeight ? point : null;
    }
    private void OnContextClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; Focus();
        var point = e.GetPosition(Points);
        if (point.X < 64 || point.Y < 24) return;
        var hit = Points.Hit(point); Select(hit);
        var menu = new ContextMenu();
        var properties = new MenuItem { Header = "Properties…", InputGestureText = "Ctrl+P", IsEnabled = hit.HasValue };
        properties.Click += (_, _) => OpenSelected(); menu.Items.Add(properties);
        ContextMenu = menu; menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private void OpenSelected()
    { if (_selectedValue is { } value) Host?.EditInstrumentChange(this, value.Tick, value.Id); }
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        long tick = DataContext switch { TimelineWorkspaceViewModel w => w.EditCursorTick ?? 0,
            InstrumentWorkspaceViewModel w => w.EditCursorTick ?? 0, _ => 0 };
        Host?.EditInstrumentChange(this, Settings?.SnapAbsolute(tick) ?? tick, null);
    }
    internal void SelectAt(long tick)
    {
        _selectTick = tick;
        // The background index resolves off-screen creation too. No raw source
        // or cold page is synchronously scanned by the UI selection callback.
        Refresh(); Points.InvalidateVisual(); Focus();
    }
}

public sealed class InstrumentChangeLaneVisual : FrameworkElement
{
    internal InstrumentChangeLane? Lane;
    private InstrumentChangeValue[] _values = [];
    internal void Clear() { _values = []; InvalidateVisual(); }
    internal void SetValues(InstrumentChangeValue[] values) { _values = values; InvalidateVisual(); }
    private int LowerBound(long tick)
    {
        int lo = 0, hi = _values.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_values[mid].Tick < tick) lo = mid + 1; else hi = mid; }
        return lo;
    }
    internal InstrumentChangeValue? Find(MidoraId? id) => _values.FirstOrDefault(value => value.Id == id) is var value && value.Id != default ? value : null;
    internal InstrumentChangeValue? AtTick(long tick) => LowerBound(tick) is int index && index < _values.Length && _values[index].Tick == tick ? _values[index] : null;
    internal InstrumentChangeValue? Hit(Point point)
    {
        if (Lane is null || Math.Abs(point.Y - (24 + (ActualHeight - 24) * .5)) > 12) return null;
        int at = LowerBound(Lane.PointTick(point.X, snap: false));
        InstrumentChangeValue? result = null; double distance = 12;
        for (int index = Math.Max(0, at - 1); index < Math.Min(_values.Length, at + 1); index++)
        { double d = Math.Abs(Lane.TickX(_values[index].Tick) - point.X); if (d <= distance) { result = _values[index]; distance = d; } }
        return result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Lane is null || ActualWidth <= 64 || ActualHeight <= 24) return;
        dc.PushClip(new RectangleGeometry(new Rect(64, 24, ActualWidth - 64, ActualHeight - 24)));
        var range = Lane.VisibleRange; int index = LowerBound(range.Start);
        double lastPixel = -1, labelEnd = -1, y = 24 + (ActualHeight - 24) * .5;
        int labels = 0; double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.UI") as FontFamily ?? EmbeddedFontFamilies.Ui,
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (; index < _values.Length && _values[index].Tick < range.End; index++)
        {
            var value = _values[index]; double x = Lane.TickX(value.Tick), pixel = Math.Floor(x * dpi);
            bool selected = value.Id == Lane.SelectedChangeId;
            if (pixel != lastPixel || selected)
                dc.DrawEllipse(selected ? Brushes.IndianRed : Brushes.SlateGray, new Pen(Brushes.LightGray, 1), new(x, y), 4, 4);
            lastPixel = pixel;
            if (labels >= 128 || x < labelEnd) continue;
            var text = new FormattedText(Lane.Label(value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 11, Brushes.LightGray, dpi) { MaxTextWidth = 240, MaxTextHeight = 18, Trimming = TextTrimming.CharacterEllipsis };
            double width = Math.Min(248, text.Width + 8);
            dc.DrawRoundedRectangle(TryFindResource("Brush.Surface.1") as Brush ?? Brushes.Black,
                new Pen(TryFindResource("Brush.Border.Strong") as Brush ?? Brushes.DimGray, 1), new(x, y + 10, width, 24), 3, 3);
            dc.DrawText(text, new(x + 4, y + 13)); labelEnd = x + width + 8; labels++;
        }
        dc.Pop();
    }
}
