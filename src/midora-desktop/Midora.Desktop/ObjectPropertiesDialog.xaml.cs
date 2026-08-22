using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class ObjectPropertiesDialog : Window
{
    private readonly DesktopSessionController? _session;
    private readonly WorkspaceViewModel? _workspace;
    private readonly ObjectPropertiesViewModel _properties;
    private readonly Func<IReadOnlyDictionary<string, string>, bool>? _customSubmit;

    public ObjectPropertiesDialog(
        DesktopSessionController session,
        WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);
        _session = session;
        _workspace = workspace;
        _properties = session.CreateObjectProperties(workspace);
        CanEdit = session.CanEditProject;
        InitializeComponent();
        DataContext = _properties;
    }

    public ObjectPropertiesDialog(
        ObjectPropertiesViewModel properties,
        Func<IReadOnlyDictionary<string, string>, bool> submit)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(submit);
        _properties = properties;
        _customSubmit = submit;
        CanEdit = true;
        InitializeComponent();
        DataContext = _properties;
    }

    public bool CanEdit { get; }

    private void OnActivateMixedClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PropertyField property })
        {
            property.ActivateMixedEdit();
        }
    }

    private void OnResetFieldClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PropertyField property })
        {
            property.Reset();
        }
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_customSubmit is not null)
            {
                Dictionary<string, string> values = _properties.Fields
                    .Where(property => property.IsEditable)
                    .ToDictionary(
                        property => property.Key,
                        property => property.Value,
                        StringComparer.Ordinal);
                if (!_customSubmit(values)) return;
            }
            else
            {
                _session!.ApplyObjectProperties(
                    _workspace!,
                    _properties.Fields.Where(property => property.HasPendingChange).ToArray());
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _properties.ErrorText = exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
