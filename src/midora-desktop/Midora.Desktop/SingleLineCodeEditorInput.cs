using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;

namespace Midora.Desktop;

internal static class SingleLineCodeEditorInput
{
    public static void Attach(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.TextArea.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, eventArgs) => ExecutePaste(editor, eventArgs),
            (_, eventArgs) => QueryPaste(editor, eventArgs)));
    }

    public static string Normalize(string? text) =>
        (text ?? string.Empty)
        .Replace("\r\n", " ", StringComparison.Ordinal)
        .Replace('\r', ' ')
        .Replace('\n', ' ');

    internal static void InsertText(TextEditor editor, string? text)
    {
        ArgumentNullException.ThrowIfNull(editor);
        editor.TextArea.Selection.ReplaceSelectionWithText(Normalize(text));
    }

    private static void QueryPaste(TextEditor editor, CanExecuteRoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (editor.IsReadOnly)
        {
            eventArgs.CanExecute = false;
            return;
        }

        try
        {
            eventArgs.CanExecute = Clipboard.ContainsText(TextDataFormat.UnicodeText);
        }
        catch (ExternalException)
        {
            eventArgs.CanExecute = false;
        }
    }

    private static void ExecutePaste(TextEditor editor, ExecutedRoutedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) return;
            InsertText(editor, Clipboard.GetText(TextDataFormat.UnicodeText));
        }
        catch (ExternalException)
        {
            // A transient clipboard ownership failure must not escape the editor input route.
        }
    }
}
