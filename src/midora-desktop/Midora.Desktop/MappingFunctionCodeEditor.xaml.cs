using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Indentation.CSharp;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Midora.Desktop;

public partial class MappingFunctionCodeEditor : UserControl
{
    public event EventHandler? TextChanged;
    public event EventHandler? CommitRequested;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MappingFunctionCodeEditor),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChangedExternally));

    public static readonly DependencyProperty CaretStatusProperty = DependencyProperty.Register(
        nameof(CaretStatus),
        typeof(string),
        typeof(MappingFunctionCodeEditor),
        new FrameworkPropertyMetadata(
            "Ln 1, Col 1",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly MappingFunctionSemanticColorizer _semanticColorizer = new();
    private readonly MappingFunctionBracketRenderer _bracketRenderer = new();
    private IReadOnlyList<MappingFunctionCompletionItem> _completionItems = [];
    private bool _synchronizing;
    private bool _refreshCompletionAfterChange;
    private int _signatureIndex;

    public MappingFunctionCodeEditor()
    {
        InitializeComponent();
        CodeEditor.Options.ConvertTabsToSpaces = true;
        CodeEditor.Options.IndentationSize = 4;
        CodeEditor.Options.EnableHyperlinks = false;
        CodeEditor.Options.EnableEmailHyperlinks = false;
        CodeEditor.Options.HighlightCurrentLine = false;
        CodeEditor.Options.AllowScrollBelowDocument = true;
        CodeEditor.TextArea.IndentationStrategy = new CSharpIndentationStrategy(CodeEditor.Options);
        CodeEditor.TextArea.TextView.LineTransformers.Add(_semanticColorizer);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_bracketRenderer);
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            UpdateCaretStatus();
            UpdateBracketMatch();
        };
        CodeEditor.TextArea.SelectionChanged += (_, _) => UpdateCaretStatus();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public string CaretStatus
    {
        get => (string)GetValue(CaretStatusProperty);
        set => SetValue(CaretStatusProperty, value ?? string.Empty);
    }

    public bool FindNext(string needle, out bool wrapped)
    {
        ArgumentNullException.ThrowIfNull(needle);
        wrapped = false;
        if (needle.Length == 0) return false;

        int start = Math.Clamp(
            CodeEditor.SelectionStart + CodeEditor.SelectionLength,
            0,
            CodeEditor.Text.Length);
        int index = CodeEditor.Text.IndexOf(
            needle,
            start,
            StringComparison.OrdinalIgnoreCase);
        if (index < 0 && start > 0)
        {
            index = CodeEditor.Text.IndexOf(
                needle,
                0,
                start,
                StringComparison.OrdinalIgnoreCase);
            wrapped = index >= 0;
        }
        if (index < 0) return false;

        _ = CodeEditor.Focus();
        CodeEditor.Select(index, needle.Length);
        CodeEditor.ScrollToLine(CodeEditor.Document.GetLineByOffset(index).LineNumber);
        return true;
    }

    public void FocusEditor() => _ = CodeEditor.Focus();

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) FocusEditor();
    }

    private static void OnTextChangedExternally(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        MappingFunctionCodeEditor editor = (MappingFunctionCodeEditor)dependencyObject;
        if (editor._synchronizing) return;
        string text = eventArgs.NewValue as string ?? string.Empty;
        if (string.Equals(editor.CodeEditor.Text, text, StringComparison.Ordinal)) return;

        int caret = Math.Min(editor.CodeEditor.CaretOffset, text.Length);
        editor._synchronizing = true;
        try
        {
            editor.CodeEditor.Text = text;
            editor.CodeEditor.CaretOffset = caret;
            editor.CodeEditor.Document.UndoStack.ClearAll();
        }
        finally
        {
            editor._synchronizing = false;
        }
        editor._semanticColorizer.Invalidate(text);
        editor.CodeEditor.TextArea.TextView.Redraw();
        editor.UpdateCaretStatus();
        editor.UpdateBracketMatch();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_synchronizing) return;
        string normalized = CodeEditor.Text.Replace('\r', ' ').Replace('\n', ' ');
        if (!string.Equals(normalized, CodeEditor.Text, StringComparison.Ordinal))
        {
            int caret = Math.Min(CodeEditor.CaretOffset, normalized.Length);
            CodeEditor.Text = normalized;
            CodeEditor.CaretOffset = caret;
            return;
        }
        _synchronizing = true;
        try
        {
            SetCurrentValue(TextProperty, CodeEditor.Text);
        }
        finally
        {
            _synchronizing = false;
        }

        _semanticColorizer.Invalidate(CodeEditor.Text);
        CodeEditor.TextArea.TextView.Redraw();
        UpdateCaretStatus();
        UpdateBracketMatch();
        TextChanged?.Invoke(this, EventArgs.Empty);
        if (_refreshCompletionAfterChange || CompletionPopup.IsOpen)
        {
            _refreshCompletionAfterChange = false;
            _ = Dispatcher.BeginInvoke(ShowCompletion, DispatcherPriority.Background);
        }
    }

    private void OnEditorPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Length == 1 && TryHandlePairInput(e.Text[0]))
        {
            e.Handled = true;
            return;
        }

        _refreshCompletionAfterChange = e.Text.Any(value =>
            char.IsLetterOrDigit(value) || value is '_' or '.');
        if (!_refreshCompletionAfterChange) CloseCompletion();
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.Space)
        {
            ShowCompletion();
            e.Handled = true;
            return;
        }

        if (CompletionPopup.IsOpen)
        {
            switch (key)
            {
                case Key.Down:
                    MoveCompletionSelection(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveCompletionSelection(-1);
                    e.Handled = true;
                    return;
                case Key.F1:
                    CycleSignature();
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter:
                    CommitCompletion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseCompletion();
                    e.Handled = true;
                    return;
            }
        }

        if (key == Key.Enter)
        {
            CommitRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _refreshCompletionAfterChange = CompletionPopup.IsOpen;
        }
    }

    private bool TryHandlePairInput(char value)
    {
        (char Open, char Close) pair = value switch
        {
            '(' => ('(', ')'),
            ')' => ('\0', ')'),
            _ => ('\0', '\0')
        };
        if (pair.Close == '\0') return false;

        int caret = CodeEditor.CaretOffset;
        string text = CodeEditor.Text;
        if (!MappingFunctionCompletionProvider.IsCodePosition(text, caret)) return false;
        if (pair.Open == '\0')
        {
            if (CodeEditor.SelectionLength == 0
                && caret < text.Length
                && text[caret] == pair.Close)
            {
                SetCaret(caret + 1);
                return true;
            }
            return false;
        }

        int start = CodeEditor.SelectionLength > 0 ? CodeEditor.SelectionStart : caret;
        string selected = CodeEditor.SelectionLength > 0 ? CodeEditor.SelectedText : string.Empty;
        CodeEditor.Document.Replace(
            start,
            CodeEditor.SelectionLength,
            pair.Open + selected + pair.Close);
        SetCaret(start + 1 + selected.Length);
        CloseCompletion();
        return true;
    }

    private void ShowCompletion()
    {
        if (!CodeEditor.IsKeyboardFocusWithin)
        {
            CloseCompletion();
            return;
        }
        IReadOnlyList<MappingFunctionCompletionItem> items =
            MappingFunctionCompletionProvider.GetCompletions(
                CodeEditor.Text,
                CodeEditor.CaretOffset);
        if (items.Count == 0)
        {
            CloseCompletion();
            return;
        }

        _completionItems = items.ToArray();
        CompletionList.ItemsSource = _completionItems;
        CompletionList.SelectedIndex = 0;
        _signatureIndex = 0;
        UpdateSignaturePanel();
        PositionCompletionPopup();
        CompletionPopup.IsOpen = true;
    }

    private void PositionCompletionPopup()
    {
        try
        {
            TextLocation location = CodeEditor.Document.GetLocation(CodeEditor.CaretOffset);
            TextViewPosition viewPosition = new(location);
            Point visual = CodeEditor.TextArea.TextView.GetVisualPosition(
                viewPosition,
                VisualYPosition.LineBottom);
            Point insideView = visual - CodeEditor.TextArea.TextView.ScrollOffset;
            Point relative = CodeEditor.TextArea.TextView.TranslatePoint(insideView, CodeEditor);
            CompletionPopup.HorizontalOffset = Math.Max(0, relative.X);
            CompletionPopup.VerticalOffset = Math.Max(0, relative.Y + 2);
        }
        catch
        {
            CompletionPopup.HorizontalOffset = 32;
            CompletionPopup.VerticalOffset = 32;
        }
    }

    private MappingFunctionCompletionItem? CurrentCompletion =>
        CompletionList.SelectedItem as MappingFunctionCompletionItem;

    private void MoveCompletionSelection(int delta)
    {
        if (_completionItems.Count == 0) return;
        CompletionList.SelectedIndex = Math.Clamp(
            CompletionList.SelectedIndex + delta,
            0,
            _completionItems.Count - 1);
        CompletionList.ScrollIntoView(CompletionList.SelectedItem);
    }

    private void CommitCompletion()
    {
        if (CurrentCompletion is not MappingFunctionCompletionItem item) return;
        int caret = CodeEditor.CaretOffset;
        int start = caret;
        string text = CodeEditor.Text;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        CloseCompletion();
        CodeEditor.Document.Replace(start, caret - start, item.InsertText);
        SetCaret(start + item.InsertText.Length - item.CaretBacktrack);
        _ = CodeEditor.Focus();
    }

    private void OnCompletionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _signatureIndex = 0;
        UpdateSignaturePanel();
    }

    private void OnCompletionListPreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ListBoxWheelScroll.ScrollOneItemPerNotch(CompletionList, e);

    private void OnCompletionMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CommitCompletion();
        e.Handled = true;
    }

    private void CycleSignature()
    {
        if (CurrentCompletion?.Signatures is not { Count: > 1 } signatures) return;
        _signatureIndex = (_signatureIndex + 1) % signatures.Count;
        UpdateSignaturePanel();
    }

    private void UpdateSignaturePanel()
    {
        if (CurrentCompletion is not MappingFunctionCompletionItem item)
        {
            SignaturePanel.Visibility = Visibility.Collapsed;
            SignatureText.Text = string.Empty;
            return;
        }
        IReadOnlyList<string> signatures = item.Signatures is { Count: > 0 }
            ? item.Signatures
            : [item.Description];
        _signatureIndex = Math.Clamp(_signatureIndex, 0, signatures.Count - 1);
        string suffix = signatures.Count > 1
            ? $"  ({_signatureIndex + 1}/{signatures.Count}, F1)"
            : string.Empty;
        SignatureText.Text = signatures[_signatureIndex] + suffix;
        SignaturePanel.Visibility = Visibility.Visible;
    }

    private void CloseCompletion()
    {
        CompletionPopup.IsOpen = false;
        CompletionList.ItemsSource = null;
        _completionItems = [];
        SignatureText.Text = string.Empty;
        SignaturePanel.Visibility = Visibility.Collapsed;
        _signatureIndex = 0;
    }

    private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!CompletionPopup.IsKeyboardFocusWithin) CloseCompletion();
    }

    private void SetCaret(int offset)
    {
        CodeEditor.CaretOffset = Math.Clamp(offset, 0, CodeEditor.Text.Length);
        CodeEditor.Select(CodeEditor.CaretOffset, 0);
        UpdateCaretStatus();
        UpdateBracketMatch();
    }

    private void UpdateCaretStatus()
    {
        TextLocation location = CodeEditor.Document.GetLocation(CodeEditor.CaretOffset);
        SetCurrentValue(CaretStatusProperty, $"Ln {location.Line}, Col {location.Column}");
    }

    private void UpdateBracketMatch()
    {
        _bracketRenderer.Match = MappingFunctionBracketMatcher.TryFind(
            CodeEditor.Text,
            CodeEditor.CaretOffset,
            out MappingFunctionBracketMatch match)
            ? match
            : null;
        CodeEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private sealed class MappingFunctionSemanticColorizer : DocumentColorizingTransformer
    {
        private static readonly Brush KeywordBrush = FrozenBrush(CodeEditorDarkPalette.Keyword);
        private static readonly Brush VariableBrush = FrozenBrush(CodeEditorDarkPalette.Variable);
        private static readonly Brush TypeBrush = FrozenBrush(CodeEditorDarkPalette.Type);
        private static readonly Brush MethodBrush = FrozenBrush(CodeEditorDarkPalette.Method);
        private static readonly Brush NumberBrush = FrozenBrush(CodeEditorDarkPalette.Number);
        private static readonly Brush OperatorBrush = FrozenBrush(CodeEditorDarkPalette.Operator);
        private static readonly Brush StringBrush = FrozenBrush(CodeEditorDarkPalette.String);
        private static readonly Brush CommentBrush = FrozenBrush(CodeEditorDarkPalette.Comment);
        private static readonly CSharpParseOptions ParseOptions = new(
            LanguageVersion.CSharp14,
            DocumentationMode.None,
            SourceCodeKind.Regular);

        private string _text = string.Empty;
        private IReadOnlyList<ColoredSpan> _spans = [];

        public void Invalidate(string text)
        {
            if (string.Equals(_text, text, StringComparison.Ordinal)) return;
            _text = text;
            _spans = CreateSpans(text);
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (ColoredSpan span in _spans)
            {
                int start = Math.Max(line.Offset, span.Start);
                int end = Math.Min(line.EndOffset, span.Start + span.Length);
                if (start >= end) continue;
                ChangeLinePart(
                    start,
                    end,
                    element => element.TextRunProperties.SetForegroundBrush(span.Brush));
            }
        }

        private static IReadOnlyList<ColoredSpan> CreateSpans(string text)
        {
            SyntaxToken[] tokens = SyntaxFactory.ParseTokens(
                    text ?? string.Empty,
                    options: ParseOptions)
                .ToArray();
            List<ColoredSpan> result = [];
            for (int index = 0; index < tokens.Length; index++)
            {
                SyntaxToken token = tokens[index];
                AddTriviaSpans(result, token.LeadingTrivia);
                AddTriviaSpans(result, token.TrailingTrivia);

                Brush? brush = GetTokenBrush(tokens, index);
                if (brush is not null && token.Span.Length > 0)
                {
                    result.Add(new(token.SpanStart, token.Span.Length, brush));
                }
            }
            return result;
        }

        private static Brush? GetTokenBrush(SyntaxToken[] tokens, int index)
        {
            SyntaxToken token = tokens[index];
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                string value = token.ValueText;
                return value switch
                {
                    "value" or "context" => VariableBrush,
                    "Math" => TypeBrush,
                    _ when MappingFunctionCompletionProvider.ContractTypeNames.Contains(value) => TypeBrush,
                    _ when HasReceiver(tokens, index, "context")
                           && MappingFunctionCompletionProvider.ContextMemberNames.Contains(value) => VariableBrush,
                    _ when HasReceiver(tokens, index, "Math")
                           && MappingFunctionCompletionProvider.MathMethodNames.Contains(value) => MethodBrush,
                    _ when HasReceiver(tokens, index, "Math")
                           && MappingFunctionCompletionProvider.MathConstantNames.Contains(value) => NumberBrush,
                    _ => null
                };
            }

            if (SyntaxFacts.GetKeywordKind(token.ValueText) != SyntaxKind.None
                || SyntaxFacts.GetContextualKeywordKind(token.ValueText) != SyntaxKind.None)
            {
                return KeywordBrush;
            }

            if (token.IsKind(SyntaxKind.NumericLiteralToken)) return NumberBrush;
            if (IsStringOrCharacterToken(token)) return StringBrush;
            return IsOperatorToken(token.Kind()) ? OperatorBrush : null;
        }

        private static void AddTriviaSpans(List<ColoredSpan> result, SyntaxTriviaList triviaList)
        {
            foreach (SyntaxTrivia trivia in triviaList)
            {
                if (!IsCommentOrDisabledText(trivia) || trivia.Span.Length <= 0) continue;
                result.Add(new(trivia.SpanStart, trivia.Span.Length, CommentBrush));
            }
        }

        private static bool IsStringOrCharacterToken(SyntaxToken token)
        {
            string kind = token.Kind().ToString();
            return kind.Contains("String", StringComparison.Ordinal)
                   || kind.Contains("CharacterLiteral", StringComparison.Ordinal)
                   || kind.Contains("Interpolated", StringComparison.Ordinal)
                      && kind.Contains("Text", StringComparison.Ordinal);
        }

        private static bool IsCommentOrDisabledText(SyntaxTrivia trivia) => trivia.Kind() is
            SyntaxKind.SingleLineCommentTrivia
            or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia
            or SyntaxKind.MultiLineDocumentationCommentTrivia
            or SyntaxKind.DisabledTextTrivia;

        private static bool IsOperatorToken(SyntaxKind kind) => kind is
            SyntaxKind.PlusToken
            or SyntaxKind.MinusToken
            or SyntaxKind.AsteriskToken
            or SyntaxKind.SlashToken
            or SyntaxKind.PercentToken
            or SyntaxKind.AmpersandToken
            or SyntaxKind.BarToken
            or SyntaxKind.CaretToken
            or SyntaxKind.ExclamationToken
            or SyntaxKind.TildeToken
            or SyntaxKind.EqualsToken
            or SyntaxKind.LessThanToken
            or SyntaxKind.GreaterThanToken
            or SyntaxKind.EqualsEqualsToken
            or SyntaxKind.ExclamationEqualsToken
            or SyntaxKind.LessThanEqualsToken
            or SyntaxKind.GreaterThanEqualsToken
            or SyntaxKind.PlusPlusToken
            or SyntaxKind.MinusMinusToken
            or SyntaxKind.AmpersandAmpersandToken
            or SyntaxKind.BarBarToken
            or SyntaxKind.QuestionToken
            or SyntaxKind.QuestionQuestionToken
            or SyntaxKind.QuestionQuestionEqualsToken
            or SyntaxKind.EqualsGreaterThanToken
            or SyntaxKind.PlusEqualsToken
            or SyntaxKind.MinusEqualsToken
            or SyntaxKind.AsteriskEqualsToken
            or SyntaxKind.SlashEqualsToken
            or SyntaxKind.PercentEqualsToken
            or SyntaxKind.AmpersandEqualsToken
            or SyntaxKind.BarEqualsToken
            or SyntaxKind.CaretEqualsToken
            or SyntaxKind.LessThanLessThanToken
            or SyntaxKind.GreaterThanGreaterThanToken
            or SyntaxKind.LessThanLessThanEqualsToken
            or SyntaxKind.GreaterThanGreaterThanEqualsToken
            or SyntaxKind.GreaterThanGreaterThanGreaterThanToken
            or SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken;

        private static bool HasReceiver(SyntaxToken[] tokens, int index, string receiver) =>
            index >= 2
            && tokens[index - 1].IsKind(SyntaxKind.DotToken)
            && string.Equals(tokens[index - 2].ValueText, receiver, StringComparison.Ordinal);

        private sealed record ColoredSpan(int Start, int Length, Brush Brush);
    }

    private sealed class MappingFunctionBracketRenderer : IBackgroundRenderer
    {
        private static readonly Brush MatchFill = FrozenBrush("#303E50");
        private static readonly Pen MatchPen = FrozenPen("#6FA7D8");
        private static readonly Brush ErrorFill = FrozenBrush("#42191D");
        private static readonly Pen ErrorPen = FrozenPen("#E5484D");

        public MappingFunctionBracketMatch? Match { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Match is not MappingFunctionBracketMatch match || !textView.VisualLinesValid) return;
            DrawBracket(textView, drawingContext, match.BracketOffset, match.IsMatched);
            if (match.IsMatched) DrawBracket(textView, drawingContext, match.MatchingOffset, true);
        }

        private static void DrawBracket(
            TextView textView,
            DrawingContext context,
            int offset,
            bool matched)
        {
            TextSegment segment = new() { StartOffset = offset, Length = 1 };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                Rect marker = new(
                    rect.X,
                    rect.Y + 1,
                    Math.Max(1, rect.Width),
                    Math.Max(1, rect.Height - 2));
                context.DrawRoundedRectangle(
                    matched ? MatchFill : ErrorFill,
                    matched ? MatchPen : ErrorPen,
                    marker,
                    2,
                    2);
            }
        }
    }

    private static Brush FrozenBrush(string value)
    {
        Brush brush = (Brush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string value)
    {
        Pen pen = new(FrozenBrush(value), 1);
        pen.Freeze();
        return pen;
    }
}
