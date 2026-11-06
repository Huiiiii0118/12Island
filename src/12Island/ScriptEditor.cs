using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace TwelveIsland;

// The custom quota script box: a code editor with JavaScript colouring drawn from the settings palette.
internal static class ScriptEditor
{
    private static readonly string[] Keywords =
    [
        "async", "await", "function", "return", "const", "let", "var", "if", "else", "for", "of", "in", "while", "do", "break", "continue",
        "switch", "case", "default", "try", "catch", "finally", "throw", "new", "typeof", "instanceof", "delete", "void", "class", "extends",
        "this", "true", "false", "null", "undefined"
    ];

    // Consolas has no Chinese glyphs. Chinese and full-width punctuation come from Microsoft YaHei UI, whose glyphs are
    // 1 em wide, scaled so each takes exactly two Consolas columns (2 × 0.5498 em); trailing comments then line up.
    private static readonly Lazy<FontFamily> CodeFont = new(() =>
    {
        var consolas = new FontFamily("Consolas");
        var family = new FontFamily { Baseline = consolas.Baseline, LineSpacing = consolas.LineSpacing };
        family.FamilyNames[System.Windows.Markup.XmlLanguage.GetLanguage("en-us")] = "TwelveIsland Code";
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "2E80-9FFF,F900-FAFF,FE30-FE4F,FF00-FF60,FFE0-FFE6", Target = "Microsoft YaHei UI", Scale = 2 * 0.5498046875 });
        family.FamilyMaps.Add(new FontFamilyMap { Unicode = "0000-10FFFF", Target = "Consolas" });
        return family;
    });

    public static (Border Frame, TextEditor Editor) Create(string text, SettingsPalette palette)
    {
        var editor = new TextEditor
        {
            Text = text, FontFamily = CodeFont.Value, FontSize = 12, Foreground = UI.Brush(palette.Primary), Background = Brushes.Transparent,
            WordWrap = false, ShowLineNumbers = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SyntaxHighlighting = Highlighting(palette.Dark)
        };
        editor.Options.ConvertTabsToSpaces = true; editor.Options.IndentationSize = 2;
        editor.Options.EnableHyperlinks = false; editor.Options.EnableEmailHyperlinks = false;
        editor.TextArea.Caret.CaretBrush = UI.Brush(palette.Accent);
        var selection = UI.Brush(palette.Dark ? "#4D88B0EE" : "#40347BDF");
        editor.TextArea.SelectionBrush = selection; editor.TextArea.SelectionBorder = null; editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionCornerRadius = 0;
        var frame = new Border
        {
            CornerRadius = new CornerRadius(6), Background = UI.Brush(palette.Input), BorderBrush = UI.Brush(palette.Border), BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8), Height = 210, Child = editor
        };
        // The frame follows the other inputs: its border takes the accent colour while the editor has focus.
        editor.IsKeyboardFocusWithinChanged += (_, _) => frame.BorderBrush = UI.Brush(editor.IsKeyboardFocusWithin ? (palette.Dark ? "#348AEB" : "#2374DE") : palette.Border);
        return (frame, editor);
    }

    private static IHighlightingDefinition Highlighting(bool dark)
    {
        string keyword = dark ? "#82AEEB" : "#1F5FBF", text = dark ? "#D9A17A" : "#A3531E", comment = dark ? "#7F8591" : "#767B87", number = dark ? "#7CC7B6" : "#1A7F6E";
        string Quoted(string mark, bool multiline) =>
            $"""<Span color="String" multiline="{(multiline ? "true" : "false")}"><Begin>{mark}</Begin><End>{mark}</End><RuleSet><Span begin="\\" end="." /></RuleSet></Span>""";
        var xshd = $"""
            <SyntaxDefinition name="TwelveIslandJavaScript" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Comment" foreground="{comment}" />
              <Color name="String" foreground="{text}" />
              <Color name="Keyword" foreground="{keyword}" />
              <Color name="Number" foreground="{number}" />
              <RuleSet>
                <Span color="Comment" begin="//" />
                <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
                {Quoted("\"", false)}
                {Quoted("'", false)}
                {Quoted("`", true)}
                <Keywords color="Keyword">{string.Concat(Keywords.Select(word => $"<Word>{word}</Word>"))}</Keywords>
                <Rule color="Number">\b0[xX][0-9a-fA-F]+|(\b\d+(\.[0-9]+)?|\.[0-9]+)([eE][+-]?[0-9]+)?</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
