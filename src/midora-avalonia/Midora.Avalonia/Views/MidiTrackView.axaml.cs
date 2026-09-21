using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Midora.Avalonia.Editing;
using Midora.Avalonia.Presentation.Controls;
using Midora.Avalonia.Presentation.Rendering;

namespace Midora.Avalonia.Views;

/// <summary>
/// Slice E track editor: one imported SMF track shown through the Avalonia
/// <see cref="TimelineSurface"/> in Notes (piano roll), Velocity, Events, or Conductor mode.
/// The view is a thin host; rendering, hit-testing, panning, zooming, selection, and editing all
/// live in the presentation surface. Until <see cref="SetProject"/> is called the surface has no
/// source and the footer shows the empty hint.
/// </summary>
public partial class MidiTrackView : UserControl
{
    private static readonly string[] SubdivisionPresets =
    [
        "Bar", "1/1", "1/2", "1/3", "1/4", "1/6", "3/16", "1/8", "1/12", "3/32",
        "1/16", "1/24", "3/64", "1/32", "1/48", "1/64", "1/128", "1/256",
    ];

    private const long DefaultTicksPerQuarterNote = 480;
    private const int DefaultFirstPitch = 36;
    private const int DefaultPitchCount = 60;
    private const double DefaultValueMinimum = 0d;
    private const double DefaultValueMaximum = 127d;
    private const double NotesLaneHeight = 15d;
    private const double ConductorLaneHeight = 20d;
    private const double HorizontalZoomStep = 1.25;

    private EditableMidiProject? _project;
    private EditableMidiSource? _source;
    private int _trackIndex;
    private long _ticksPerQuarterNote = DefaultTicksPerQuarterNote;
    private TimelineSurfaceMode _mode = TimelineSurfaceMode.PianoRoll;
    private bool _suppressTrackSelection;

    public MidiTrackView()
    {
        InitializeComponent();

        SubdivisionBox.ItemsSource = SubdivisionPresets;
        SubdivisionBox.Text = "1/8";

        Timeline.PointerTickChanged += OnPointerTickChanged;
        Timeline.PlaybackCursorRequested += (_, tick) =>
            PlaybackCursorRequested?.Invoke(this, tick);
        Timeline.EditCursorRequested += (_, tick) => EditCursorRequested?.Invoke(this, tick);
        Timeline.TimeRangeSelected += (_, range) => TimeRangeSelected?.Invoke(this, range);
        Timeline.PropertyChanged += (_, change) =>
        {
            if (change.Property == TimelineSurface.FirstLaneProperty)
            {
                Keyboard.FirstLane = Timeline.FirstLane;
            }
        };
        Timeline.SelectionChanged += OnSelectionChanged;
        Timeline.LaneActivated += OnLaneActivated;
        Timeline.EditCommitted += OnEditCommitted;
        Keyboard.KeyPressed += OnKeyboardKeyPressed;
        Keyboard.KeyReleased += (_, _) => { };

        SetTool(TimelineToolMode.Select);
        SetToolbarEnabled(false);
        SetMode(TimelineSurfaceMode.PianoRoll);
        ShowPointerTick(-1);
    }

    /// <summary>
    /// Raised after the surface commits an edit gesture to the project.
    /// </summary>
    public event EventHandler? Edited;

    /// <summary>Raised when the ruler asks to move the Playback Cursor (SRS 20.1.3).</summary>
    public event EventHandler<long>? PlaybackCursorRequested;

    /// <summary>Raised when the ruler or an empty content click asks to move the Edit Cursor.</summary>
    public event EventHandler<long>? EditCursorRequested;

    /// <summary>Raised when a ruler drag completes a Time Range Selection.</summary>
    public event EventHandler<TimelineTimeRangeEventArgs>? TimeRangeSelected;

    /// <summary>
    /// Binds an editable MIDI project to the editor. The combo lists every non-conductor
    /// project track; <paramref name="trackIndex"/> is the zero-based project track index.
    /// </summary>
    public void SetProject(EditableMidiProject project, int trackIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!ReferenceEquals(_project, project))
        {
            if (_project is not null)
            {
                _project.Changed -= OnProjectChanged;
            }

            _project = project;
            _project.Changed += OnProjectChanged;
        }

        _ticksPerQuarterNote = Math.Max(1, project.Source.TicksPerQuarterNote);
        PopulateTracks(project, trackIndex);
        SetToolbarEnabled(true);
        ApplyTrack();
    }

    public bool Undo()
    {
        if (_project is null || !_project.CanUndo)
        {
            return false;
        }

        _project.Undo();
        Timeline.InvalidateVisual();
        return true;
    }

    public bool Redo()
    {
        if (_project is null || !_project.CanRedo)
        {
            return false;
        }

        _project.Redo();
        Timeline.InvalidateVisual();
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsTextInputFocused())
        {
            base.OnKeyDown(e);
            return;
        }

        bool control = OperatingSystem.IsMacOS()
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control && e.Key == Key.Z)
        {
            if (shift ? Redo() : Undo())
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.S:
                    SetTool(TimelineToolMode.Select);
                    e.Handled = true;
                    return;
                case Key.D:
                    SetTool(TimelineToolMode.Draw);
                    e.Handled = true;
                    return;
                case Key.E:
                    SetTool(TimelineToolMode.Erase);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }

    private void OnProjectChanged(object? sender, EventArgs e) => Timeline.InvalidateVisual();

    private void OnEditCommitted(object? sender, EventArgs e)
    {
        Timeline.InvalidateVisual();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private bool IsTextInputFocused()
    {
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or ComboBox;
    }

    private void PopulateTracks(EditableMidiProject project, int requestedIndex)
    {
        _suppressTrackSelection = true;
        try
        {
            TrackCombo.Items.Clear();
            for (int index = 0; index < project.TrackCount; index++)
            {
                TrackCombo.Items.Add(new ComboBoxItem
                {
                    Content = TrackDisplayName(project, index),
                    Tag = index
                });
            }

            _trackIndex = project.TrackCount == 0
                ? 0
                : Math.Clamp(requestedIndex, 0, project.TrackCount - 1);
            TrackCombo.SelectedIndex = project.TrackCount == 0 ? -1 : _trackIndex;
        }
        finally
        {
            _suppressTrackSelection = false;
        }
    }

    private static string TrackDisplayName(EditableMidiProject project, int trackIndex)
    {
        string name = project.Source.Tracks[trackIndex].Name;
        return string.IsNullOrWhiteSpace(name)
            ? "Track " + (trackIndex + 1).ToString(CultureInfo.InvariantCulture)
            : name;
    }

    private void ApplyTrack()
    {
        if (_project is null)
        {
            _source = null;
            Timeline.Source = null;
            Timeline.EditHost = null;
            ShowPointerTick(-1);
            return;
        }

        if (_project.TrackCount == 0)
        {
            _source = null;
            Timeline.Source = null;
            Timeline.EditHost = null;
            ShowPointerTick(-1);
            return;
        }

        _trackIndex = Math.Clamp(_trackIndex, 0, _project.TrackCount - 1);
        LengthBox.Text = Math.Max(1, _ticksPerQuarterNote).ToString(CultureInfo.InvariantCulture);

        EditableMidiSource source = new(_project, _trackIndex);
        _source = source;
        Timeline.Source = source;
        Timeline.EditHost = new EditableMidiEditHost(_project, source, _ticksPerQuarterNote);
        Timeline.PreviewProvider = null;
        Timeline.TicksPerQuarterNote = _ticksPerQuarterNote;
        Timeline.TrackNames = static lane =>
            lane < 256 ? "Ch " + lane.ToString(CultureInfo.InvariantCulture) : null;
        Timeline.FirstPitch = DefaultFirstPitch;
        Timeline.PitchCount = DefaultPitchCount;
        Timeline.FirstLane = DefaultFirstPitch;
        Timeline.LaneCount = 128;
        Keyboard.FirstLane = DefaultFirstPitch;
        Keyboard.LaneHeight = Timeline.LaneHeight;
        Timeline.ValueMinimum = DefaultValueMinimum;
        Timeline.ValueMaximum = DefaultValueMaximum;
        Timeline.TickSpan = Math.Max(1, _ticksPerQuarterNote * 4L);
        if (long.TryParse(
                Environment.GetEnvironmentVariable("MIDORA_TRACK_SPAN"),
                out long requestedSpan) && requestedSpan > 0)
        {
            // Review-only: starts the track view at a given span so zoomed-out frame cost is
            // measurable without interactive input.
            Timeline.TickSpan = requestedSpan;
        }

        Timeline.StartTick = 0;
        Timeline.SelectedId = null;

        SetMode(DefaultModeForTrack(_trackIndex));
        ShowPointerTick(-1);
        if (int.TryParse(
                Environment.GetEnvironmentVariable("MIDORA_TRACK_BENCH"),
                out int benchFrames) && benchFrames > 0)
        {
            // Review-only: runs after layout so the offscreen benchmark sees real bounds.
            Dispatcher.UIThread.Post(
                () =>
                {
                    Timeline.BenchmarkRenderFrames(2);
                    (double average, double median, double maximum, bool hasBounds) =
                        Timeline.BenchmarkRenderFrames(benchFrames);
                    if (Environment.GetEnvironmentVariable("MIDORA_TRACK_BENCH_AB") == "1")
                    {
                        int budget = TimelineSurface.GpuNoteBatchThreshold;
                        int lodMinimum = TimelineSurface.PianoRollLodMinimumNotes;
                        TimelineSurface.GpuNoteBatchThreshold = int.MaxValue;
                        TimelineSurface.PianoRollLodMinimumNotes = int.MaxValue;
                        Timeline.ClearGpuBatchCaches();
                        Timeline.BenchmarkRenderFrames(2);
                        (double shapeAverage, double shapeMedian, double shapeMaximum, _) =
                            Timeline.BenchmarkRenderFrames(benchFrames);
                        TimelineSurface.GpuNoteBatchThreshold = budget;
                        TimelineSurface.PianoRollLodMinimumNotes = lodMinimum;
                        Timeline.ClearGpuBatchCaches();
                        Console.Out.WriteLine(
                            $"MIDORA-TRACK-BENCH-AB gpuAvg={average:F2} gpuP50={median:F2} "
                            + $"gpuMax={maximum:F1} shapeAvg={shapeAverage:F2} "
                            + $"shapeP50={shapeMedian:F2} shapeMax={shapeMaximum:F1}");
                        Console.Out.Flush();
                    }
                    (int rollBatches, long rollVertexBytes, int rollVisible, long rollHits, long rollMisses) =
                        Timeline.PianoRollBatchDiagnostics;
                    (long spanTicks, long gridMs, long rulerMs, long modeMs) =
                        Timeline.ModePhaseDiagnostics;
                    Console.Out.WriteLine(
                        $"MIDORA-TRACK-BENCH frames={benchFrames} hasBounds={hasBounds} "
                        + $"avg={average:F2} ms p50={median:F2} ms max={maximum:F1} ms "
                        + $"span={Timeline.TickSpan} mode={Timeline.SurfaceMode} "
                        + $"rollVisible={rollVisible} rollBatches={rollBatches} "
                        + $"rollVertexMB={rollVertexBytes / (1024.0 * 1024.0):F1} "
                        + $"rollHits={rollHits} rollMisses={rollMisses} "
                        + $"viewSpan={spanTicks} grid={gridMs} ruler={rulerMs} modeMs={modeMs}");
                    Console.Out.Flush();
                },
                DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// The default surface mode for a track: notes when the track has notes, otherwise the
    /// event lane for event-only tracks; empty tracks keep the piano roll default.
    /// </summary>
    private TimelineSurfaceMode DefaultModeForTrack(int trackIndex)
    {
        if (_project is null || trackIndex < 0 || trackIndex >= _project.TrackCount)
        {
            return TimelineSurfaceMode.PianoRoll;
        }

        EditableMidiTrack track = _project.Tracks[trackIndex];
        if (track.Notes.Count != 0)
        {
            return TimelineSurfaceMode.PianoRoll;
        }

        return track.Events.Count != 0
            ? TimelineSurfaceMode.EventLanes
            : TimelineSurfaceMode.PianoRoll;
    }

    /// <summary>Review helper: selects a surface mode by name.</summary>
    public void ApplyModeByName(string name)
    {
        TimelineSurfaceMode mode = name.ToLowerInvariant() switch
        {
            "velocity" => TimelineSurfaceMode.Velocity,
            "events" => TimelineSurfaceMode.EventLanes,
            "conductor" => TimelineSurfaceMode.Conductor,
            _ => TimelineSurfaceMode.PianoRoll
        };
        SetMode(mode);
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
        Keyboard.LaneHeight = Timeline.LaneHeight;
        Keyboard.FirstLane = Timeline.FirstLane;
        Keyboard.IsVisible = _mode is TimelineSurfaceMode.PianoRoll or TimelineSurfaceMode.Velocity;
    }

    private void SetTool(TimelineToolMode tool)
    {
        Timeline.ToolMode = tool;
        SelectButton.IsChecked = tool == TimelineToolMode.Select;
        DrawButton.IsChecked = tool == TimelineToolMode.Draw;
        EraseButton.IsChecked = tool == TimelineToolMode.Erase;
        SplitButton.IsChecked = tool == TimelineToolMode.Split;
    }

    private void SetToolbarEnabled(bool enabled)
    {
        TrackCombo.IsEnabled = enabled;
        NotesButton.IsEnabled = enabled;
        VelocityButton.IsEnabled = enabled;
        EventsButton.IsEnabled = enabled;
        ConductorButton.IsEnabled = enabled;
        GridToggle.IsEnabled = enabled;
        SnapToggle.IsEnabled = enabled;
        SubdivisionBox.IsEnabled = enabled;
        LengthBox.IsEnabled = enabled;
        VelocityBox.IsEnabled = enabled;
        SelectButton.IsEnabled = enabled;
        DrawButton.IsEnabled = enabled;
        EraseButton.IsEnabled = enabled;
        SplitButton.IsEnabled = enabled;
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
    }

    private void OnTrackSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTrackSelection || _project is null)
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

    private void OnToolClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }
            || !Enum.TryParse(tag, out TimelineToolMode tool))
        {
            return;
        }

        SetTool(tool);
    }

    private void OnVerticalZoomInClick(object? sender, RoutedEventArgs e) =>
        SetVerticalLaneHeight(Timeline.LaneHeight * 1.25);

    private void OnVerticalZoomOutClick(object? sender, RoutedEventArgs e) =>
        SetVerticalLaneHeight(Timeline.LaneHeight / 1.25);

    private void SetVerticalLaneHeight(double laneHeight)
    {
        Timeline.LaneHeight = Math.Clamp(laneHeight, 3, 128);
        Keyboard.LaneHeight = Timeline.LaneHeight;
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

    public void SetSegmentRange(long startTick, long spanTicks)
    {
        Timeline.StartTick = Math.Max(0, startTick);
        Timeline.TickSpan = Math.Max(1, spanTicks);
        ShowPointerTick(-1);
    }

    public void SetPlaybackTick(long tick) => Timeline.PlaybackTick = tick;

    public void SetEditCursorTick(long tick) => Timeline.EditCursorTick = tick;

    public void SetTimeRange(long startTick, long endTick)
    {
        Timeline.TimeRangeStartTick = startTick;
        Timeline.TimeRangeEndTick = endTick;
    }

    public void SetOperationStepTicks(long stepTicks) =>
        Timeline.OperationStepTicks = Math.Max(1, stepTicks);

    private void OnPointerTickChanged(object? sender, long tick) => ShowPointerTick(tick);

    private void OnKeyboardKeyPressed(object? sender, int pitch)
    {
        // Instrument audition is not wired to the audio backend yet; the key press still
        // highlights the pressed key through PianoKeyboardStrip.
    }

    private void OnSelectionChanged(object? sender, TimelineRenderItem? item)
    {
    }

    private void OnLaneActivated(object? sender, int lane)
    {
    }

    private void ShowPointerTick(long tick)
    {
        bool visible = tick >= 0;
        PointerText.IsVisible = visible;
        PointerSeparator.IsVisible = visible;
        PointerText.Text = visible
            ? tick.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }
}
