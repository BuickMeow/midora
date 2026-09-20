using Avalonia.Controls;
using Avalonia.Media;

namespace Midora.Avalonia.Session;

public enum WorkspaceKind
{
    Arrangement,
    Diagnostics,
    AllTracks
}

/// <summary>
/// Session-scoped workspace tab identity. The Content is a placeholder control until the
/// timeline presentation core is ported; the tab/navigation semantics mirror the SRS.
/// </summary>
public sealed class WorkspaceTab
{
    public WorkspaceTab(WorkspaceKind kind, string header, Control content, Geometry? icon, bool canClose)
    {
        Kind = kind;
        Header = header;
        Content = content;
        Icon = icon;
        CanClose = canClose;
    }

    public WorkspaceKind Kind { get; }

    public string Header { get; }

    public Control Content { get; }

    public Geometry? Icon { get; }

    public bool CanClose { get; }
}
