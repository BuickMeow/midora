using System;
using System.Globalization;
using Avalonia.Controls;
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
    private const long DefaultTicksPerQuarterNote = 480;
    private const int DefaultFirstPitch = 36;
    private const int DefaultPitchCount = 60;
    private const double DefaultValueMinimum = 0d;
    private const double DefaultValueMaximum = 127d;
    private const double NotesLaneHeight = 12d;
    private const double ConductorLaneHeight = 20d;
    private const double HorizontalZoomStep = 1.25;
    private const string TimelineHint =
        "Wheel: pan · Ctrl+wheel: zoom · Drag: select · Middle-drag: pan · S/D/E: tool · Ctrl+Z: undo";

    private EditableMidiProject? _project;
    private EditableMidiSource? _source;
    private int _trackIndex;
    private long _ticksPerQuarterNote = DefaultTicksPerQuarterNote;
    private TimelineSurfaceMode _mode = TimelineSurfaceMode.PianoRoll;
    private TimelineToolMode _tool = TimelineToolMode.Select;
    private bool _suppressTrackSelection;
    private string? _selectionText;
    private string? _laneText;

    public MidiTrackView()
    {
        InitializeComponent();

        Timeline.PointerTickChanged += OnPointerTickChanged;
        Timeline.SelectionChanged += OnSelectionChanged;
        Timeline.LaneActivated += OnLaneActivated;
        Timeline.EditCommitted += OnEditCommitted;

        SetTool(TimelineToolMode.Select);
        SetToolbarEnabled(false);
        SetMode(TimelineSurfaceMode.PianoRoll);
        UpdateTickReadout(0);
        RefreshFooter();
    }

    /// <summary>
    /// Raised after the surface commits an edit gesture to the project.
    /// </summary>
    public event EventHandler? Edited;

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
            RefreshFooter();
            return;
        }

        if (_project.TrackCount == 0)
        {
            _source = null;
            Timeline.Source = null;
            Timeline.EditHost = null;
            _selectionText = null;
            _laneText = null;
            RefreshFooter();
            return;
        }

        _trackIndex = Math.Clamp(_trackIndex, 0, _project.TrackCount - 1);
        _selectionText = null;
        _laneText = null;

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

    private void SetTool(TimelineToolMode tool)
    {
        _tool = tool;
        Timeline.ToolMode = tool;
        SelectButton.IsChecked = tool == TimelineToolMode.Select;
        DrawButton.IsChecked = tool == TimelineToolMode.Draw;
        EraseButton.IsChecked = tool == TimelineToolMode.Erase;
        SplitButton.IsChecked = tool == TimelineToolMode.Split;
        RefreshFooter();
    }

    private void SetToolbarEnabled(bool enabled)
    {
        TrackCombo.IsEnabled = enabled;
        NotesButton.IsEnabled = enabled;
        VelocityButton.IsEnabled = enabled;
        EventsButton.IsEnabled = enabled;
        ConductorButton.IsEnabled = enabled;
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

    private string ToolLabel => _tool switch
    {
        TimelineToolMode.Draw => "Tool: Draw",
        TimelineToolMode.Erase => "Tool: Erase",
        TimelineToolMode.Split => "Tool: Split",
        _ => "Tool: Select"
    };

    private void RefreshFooter()
    {
        string tool = ToolLabel;
        if (_project is null)
        {
            FooterText.Text = tool + " · No MIDI project loaded.";
            return;
        }

        if (_project.TrackCount == 0)
        {
            FooterText.Text = tool + " · No MIDI tracks in this project.";
            return;
        }

        if (_selectionText is { } selection)
        {
            FooterText.Text = tool + " · " + selection;
            return;
        }

        FooterText.Text = tool + " · " + (_laneText ?? TimelineHint);
    }
}
