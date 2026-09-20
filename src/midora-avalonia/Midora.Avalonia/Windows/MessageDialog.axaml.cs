using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Midora.Avalonia.Controls;

namespace Midora.Avalonia.Windows;

public enum MessageDialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel
}

public enum MessageDialogIcon
{
    None,
    Information,
    Warning,
    Error,
    Question
}

public enum MessageDialogResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No
}

public partial class MessageDialog : Window
{
    private readonly TaskCompletionSource _closed = new();
    private readonly MessageDialogButtons _buttons;
    private MessageDialogResult _result;

    public MessageDialog()
        : this(
            "Sample message: the project compiled without diagnostics.",
            "Midora",
            MessageDialogButtons.OkCancel,
            MessageDialogIcon.Information)
    {
    }

    private MessageDialog(string message, string title, MessageDialogButtons buttons, MessageDialogIcon icon)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? "Midora" : title;
        MessageText.Text = message ?? string.Empty;
        _buttons = buttons;
        string iconKey = icon switch
        {
            MessageDialogIcon.Error => "Fluent.ErrorCircle20Regular",
            MessageDialogIcon.Warning => "Fluent.Warning20Regular",
            _ => "Fluent.Info20Regular"
        };
        MessageIcon.Data = this.FindResource(iconKey) as Geometry;
        MessageIcon.Foreground = icon switch
        {
            MessageDialogIcon.Warning => FindBrush("Brush.Warning", Color.FromRgb(220, 164, 58)),
            MessageDialogIcon.Information or MessageDialogIcon.Question =>
                FindBrush("Brush.Info", Color.FromRgb(105, 151, 188)),
            MessageDialogIcon.None => FindBrush("Brush.Text.Secondary", Color.FromRgb(151, 161, 174)),
            _ => FindBrush("Brush.Red.Hover", Color.FromRgb(242, 82, 89))
        };
        BuildButtons(buttons);
        Closed += (_, _) => _closed.TrySetResult();
    }

    public MessageDialogResult Result => _result;

    // Avalonia has no Window.DialogResult; the result is read from Result after the dialog closes.
    public static async Task<MessageDialogResult> ShowAsync(
        Window? owner,
        string message,
        string title,
        MessageDialogButtons buttons = MessageDialogButtons.Ok,
        MessageDialogIcon icon = MessageDialogIcon.Information)
    {
        MessageDialog dialog = new(message, title, buttons, icon);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
            await dialog._closed.Task;
        }
        return dialog._result;
    }

    private void BuildButtons(MessageDialogButtons buttons)
    {
        switch (buttons)
        {
            case MessageDialogButtons.Ok:
                AddButton("OK", MessageDialogResult.Ok, isDefault: true, isCancel: true);
                break;
            case MessageDialogButtons.OkCancel:
                AddButton("Cancel", MessageDialogResult.Cancel, isDefault: false, isCancel: true);
                AddButton("OK", MessageDialogResult.Ok, isDefault: true, isCancel: false, primary: true);
                break;
            case MessageDialogButtons.YesNo:
                AddButton("No", MessageDialogResult.No, isDefault: false, isCancel: true);
                AddButton("Yes", MessageDialogResult.Yes, isDefault: true, isCancel: false, primary: true);
                break;
            case MessageDialogButtons.YesNoCancel:
                AddButton("Cancel", MessageDialogResult.Cancel, isDefault: false, isCancel: true);
                AddButton("No", MessageDialogResult.No, isDefault: false, isCancel: false);
                AddButton("Yes", MessageDialogResult.Yes, isDefault: true, isCancel: false, primary: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(buttons));
        }
    }

    private void AddButton(
        string label,
        MessageDialogResult result,
        bool isDefault,
        bool isCancel,
        bool primary = false)
    {
        Button button = new()
        {
            Margin = ButtonPanel.Children.Count == 0 ? new Thickness() : new Thickness(6, 0, 0, 0),
            Content = label,
            IsDefault = isDefault,
            IsCancel = isCancel,
            Width = 112
        };
        if (isDefault || primary)
        {
            button.Classes.Add("primary");
        }
        button.Click += (_, _) => Complete(result);
        ButtonPanel.Children.Add(button);
    }

    private IBrush FindBrush(string key, Color fallback) =>
        this.FindResource(key) as IBrush ?? new SolidColorBrush(fallback);

    private void Complete(MessageDialogResult result)
    {
        _result = result;
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Complete(_buttons switch
    {
        MessageDialogButtons.Ok => MessageDialogResult.Ok,
        MessageDialogButtons.YesNo => MessageDialogResult.No,
        _ => MessageDialogResult.Cancel
    });

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
