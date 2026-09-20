using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace Midora.Avalonia.Session;

public enum WorkspaceKind
{
    Arrangement,
    Diagnostics,
    AllTracks,
    MidiTrack
}

/// <summary>
/// Session-scoped workspace tab identity. Content is the workspace control; the tab strip
/// (a horizontal ItemsControl, matching the approved WPF single-line tab strip) reflects
/// <see cref="IsActive"/> for the selected-tab underline.
/// </summary>
public sealed class WorkspaceTab : INotifyPropertyChanged
{
    private bool _isActive;

    public WorkspaceTab(WorkspaceKind kind, string header, Control content, Geometry? icon, bool canClose, int trackIndex = -1, long segmentStartTick = -1)
    {
        Kind = kind;
        Header = header;
        Content = content;
        Icon = icon;
        CanClose = canClose;
        TrackIndex = trackIndex;
        SegmentStartTick = segmentStartTick;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WorkspaceKind Kind { get; }

    public string Header { get; }

    public Control Content { get; }

    public Geometry? Icon { get; }

    public bool CanClose { get; }

    /// <summary>True while this tab is the active workspace (drives the red tab underline).</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    /// <summary>Zero-based MIDI track index for <see cref="WorkspaceKind.MidiTrack"/>; otherwise -1.</summary>
    public int TrackIndex { get; }

    /// <summary>Segment start tick for segment-scoped MIDI workspaces; otherwise -1.</summary>
    public long SegmentStartTick { get; }

    internal void SetActive(bool value) => IsActive = value;

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
