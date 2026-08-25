using System.Globalization;
using System.Windows.Data;

namespace Midora.Desktop;

public sealed class WorkspaceTabIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not WorkspaceTabIconKind kind) return null;
        string? resourceKey = kind switch
        {
            WorkspaceTabIconKind.Arrangement => "Fluent.MoviesAndTv20Regular",
            WorkspaceTabIconKind.EventInstrument => "Fluent.Guitar20Regular",
            WorkspaceTabIconKind.Settings => "Fluent.Settings20Regular",
            WorkspaceTabIconKind.LogicalTrack => "Fluent.MusicNote220Regular",
            WorkspaceTabIconKind.PureMidiTrack => "Fluent.Midi20Regular",
            WorkspaceTabIconKind.Conductor => "Fluent.Wrench20Regular",
            WorkspaceTabIconKind.Diagnostics => "Fluent.Pulse20Regular",
            _ => null
        };
        return resourceKey is null
            ? null
            : System.Windows.Application.Current?.TryFindResource(resourceKey);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
