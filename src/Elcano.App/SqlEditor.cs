using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;

namespace Elcano.App;

public sealed class SqlEditor : TextEditor
{
    public static readonly DependencyProperty BoundTextProperty = DependencyProperty.Register(nameof(BoundText), typeof(string), typeof(SqlEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (sender, args) =>
        {
            var editor = (SqlEditor)sender;
            var text = (string?)args.NewValue ?? "";
            if (editor.Text != text) editor.Text = text;
        }));
    public string BoundText { get => (string)GetValue(BoundTextProperty); set => SetValue(BoundTextProperty, value); }
    public int CaretIndex { get => CaretOffset; set => CaretOffset = value; }
    public event RoutedEventHandler? SelectionChanged;

    public SqlEditor()
    {
        Options.ConvertTabsToSpaces = true; Options.IndentationSize = 4;
        Options.AllowScrollBelowDocument = false;
        TextArea.Caret.PositionChanged += (_, _) => SelectionChanged?.Invoke(this, new RoutedEventArgs());
        TextArea.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, new RoutedEventArgs());
        Loaded += (_, _) => { ThemeManager.ThemeChanged += UpdatePalette; UpdatePalette(); };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= UpdatePalette;
    }
    protected override void OnTextChanged(EventArgs e)
    {
        if (BoundText != Text) SetCurrentValue(BoundTextProperty, Text);
        base.OnTextChanged(e);
    }
    public Rect GetRectFromCharacterIndex(int index, bool trailingEdge)
    {
        var view = TextArea.TextView;
        if (!view.IsVisible || Document is null) return Rect.Empty;
        view.EnsureVisualLines();
        var position = view.GetVisualPosition(new TextViewPosition(Document.GetLocation(index)), VisualYPosition.LineBottom) - view.ScrollOffset;
        var point = view.TranslatePoint(position, this);
        return new Rect(point.X, point.Y - view.DefaultLineHeight, 1, view.DefaultLineHeight);
    }
    private void UpdatePalette()
    {
        Brush Brush(string key) => (Brush)Application.Current.Resources[key];
        string Color(string key) => ((SolidColorBrush)Brush(key)).Color.ToString();
        TextArea.Caret.CaretBrush = Brush("Ink");
        TextArea.SelectionBrush = Brush("SelectionBrush"); TextArea.SelectionForeground = Brush("Ink");
        TextArea.SelectionBorder = null;
        var definition = $$"""
            <SyntaxDefinition name="Elcano SQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Keyword" foreground="{{Color("SqlKeyword")}}" fontWeight="bold"/>
              <Color name="String" foreground="{{Color("SqlString")}}"/>
              <Color name="Number" foreground="{{Color("SqlNumber")}}"/>
              <Color name="Comment" foreground="{{Color("SqlComment")}}"/>
              <Color name="Identifier" foreground="{{Color("SqlIdentifier")}}"/>
              <Color name="Function" foreground="{{Color("SqlFunction")}}"/>
              <RuleSet ignoreCase="true">
                <Span color="Comment" begin="--"/>
                <Span color="Comment" begin="/\*" end="\*/" multiline="true"/>
                <Span color="String" begin="'" end="'" multiline="true"/>
                <Span color="String" begin="\$\$" end="\$\$" multiline="true"/>
                <Span color="Identifier" begin="&quot;" end="&quot;"/>
                <Keywords color="Keyword">
                  <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>WITH</Word><Word>AS</Word>
                  <Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>NULL</Word><Word>IS</Word>
                  <Word>TRUE</Word><Word>FALSE</Word><Word>DISTINCT</Word><Word>ALL</Word><Word>IN</Word>
                  <Word>BETWEEN</Word><Word>LIKE</Word><Word>ILIKE</Word><Word>ESCAPE</Word>
                  <Word>GROUP</Word><Word>BY</Word><Word>ORDER</Word><Word>HAVING</Word><Word>LIMIT</Word>
                  <Word>OFFSET</Word><Word>ASC</Word><Word>DESC</Word><Word>NULLS</Word><Word>FIRST</Word><Word>LAST</Word>
                  <Word>JOIN</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>FULL</Word><Word>INNER</Word>
                  <Word>OUTER</Word><Word>CROSS</Word><Word>ON</Word><Word>USING</Word><Word>UNION</Word>
                  <Word>INTERSECT</Word><Word>EXCEPT</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word>
                  <Word>ELSE</Word><Word>END</Word><Word>OVER</Word><Word>PARTITION</Word><Word>QUALIFY</Word>
                  <Word>WINDOW</Word><Word>FILTER</Word><Word>EXISTS</Word><Word>RECURSIVE</Word><Word>CAST</Word>
                  <Word>TRY_CAST</Word><Word>VARCHAR</Word><Word>INTEGER</Word><Word>BIGINT</Word><Word>DOUBLE</Word>
                  <Word>DECIMAL</Word><Word>DATE</Word><Word>TIMESTAMP</Word><Word>INTERVAL</Word><Word>BOOLEAN</Word>
                </Keywords>
                <Rule color="Function">\b[\p{L}_][\p{L}\p{Nd}_]*(?=\s*\()</Rule>
                <Rule color="Number">\b[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\b</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(definition));
        SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
