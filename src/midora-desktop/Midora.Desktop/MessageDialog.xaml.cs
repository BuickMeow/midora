using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Midora.Desktop;

public partial class MessageDialog : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result;

    private MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? "Midora" : title;
        MessageText.Text = message ?? string.Empty;
        _buttons = buttons;
        string iconKey = image switch
        {
            MessageBoxImage.Error => "Fluent.ErrorCircle20Regular",
            MessageBoxImage.Warning => "Fluent.Warning20Regular",
            _ => "Fluent.Info20Regular"
        };
        MessageIcon.SetResourceReference(Midora.Desktop.Presentation.Controls.FluentIcon.DataProperty, iconKey);
        MessageIcon.Foreground = image switch
        {
            MessageBoxImage.Warning => FindBrush("Brush.Warning", Color.FromRgb(220, 164, 58)),
            MessageBoxImage.Information or MessageBoxImage.Question =>
                FindBrush("Brush.Info", Color.FromRgb(105, 151, 188)),
            MessageBoxImage.None => FindBrush("Brush.Text.Secondary", Color.FromRgb(151, 161, 174)),
            _ => FindBrush("Brush.Red.Hover", Color.FromRgb(242, 82, 89))
        };
        BuildButtons(buttons);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        MessageDialog dialog = new(message, title, buttons, image);
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        _ = dialog.ShowDialog();
        return dialog._result;
    }

    public static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        Show(System.Windows.Application.Current?.MainWindow, message, title, buttons, image);

    private void BuildButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, isDefault: false, isCancel: true);
                AddButton("OK", MessageBoxResult.OK, isDefault: true, isCancel: false, primary: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("No", MessageBoxResult.No, isDefault: false, isCancel: true);
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isCancel: false, primary: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, isDefault: false, isCancel: true);
                AddButton("No", MessageBoxResult.No, isDefault: false, isCancel: false);
                AddButton("Yes", MessageBoxResult.Yes, isDefault: true, isCancel: false, primary: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(buttons));
        }
    }

    private void AddButton(
        string label,
        MessageBoxResult result,
        bool isDefault,
        bool isCancel,
        bool primary = false)
    {
        Button button = new()
        {
            Margin = ButtonPanel.Children.Count == 0 ? new Thickness() : new Thickness(6, 0, 0, 0),
            Content = label,
            IsDefault = isDefault,
            IsCancel = isCancel
        };
        if (isDefault || primary)
        {
            button.SetResourceReference(StyleProperty, "Button.Dialog.Confirm");
        }
        else if (isCancel)
        {
            button.SetResourceReference(StyleProperty, "Button.Dialog.Cancel");
        }
        else
        {
            button.Width = 112;
        }
        button.Click += (_, _) => Complete(result);
        ButtonPanel.Children.Add(button);
    }

    private Brush FindBrush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private void Complete(MessageBoxResult result)
    {
        _result = result;
        DialogResult = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Complete(_buttons switch
    {
        MessageBoxButton.OK => MessageBoxResult.OK,
        MessageBoxButton.YesNo => MessageBoxResult.No,
        _ => MessageBoxResult.Cancel
    });

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
