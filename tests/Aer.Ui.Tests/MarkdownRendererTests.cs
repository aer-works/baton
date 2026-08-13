using System.Collections.Generic;
using System.Linq;
using Aer.Ui.Markdown;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Aer.Ui.Tests;

public class MarkdownRendererTests
{
    [AvaloniaFact]
    public void Empty_or_null_input_returns_empty_panel_without_throwing()
    {
        var nullResult = MarkdownRenderer.Render(null);
        Assert.IsType<StackPanel>(nullResult);
        Assert.Empty(((StackPanel)nullResult).Children);

        var emptyResult = MarkdownRenderer.Render(string.Empty);
        Assert.IsType<StackPanel>(emptyResult);
        Assert.Empty(((StackPanel)emptyResult).Children);
    }

    [AvaloniaFact]
    public void Fenced_code_block_renders_as_border_with_monospace_selectable_text_block()
    {
        const string markdown = "```csharp\nvar x = 42;\n```";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        var border = allControls.OfType<Border>().FirstOrDefault();
        Assert.NotNull(border);

        // #1076 review fix #1: the code block is wrapped in a horizontal ScrollViewer so a long line
        // scrolls instead of forcing the message column wide.
        var scrollViewer = border.Child as ScrollViewer;
        Assert.NotNull(scrollViewer);
        Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.HorizontalScrollBarVisibility);

        var selectableTextBlock = scrollViewer.Content as SelectableTextBlock;
        Assert.NotNull(selectableTextBlock);
        Assert.Equal("var x = 42;", selectableTextBlock.Text?.TrimEnd());
        AssertIsTheShippedCodeFace(selectableTextBlock.FontFamily);
    }

    [AvaloniaFact]
    public void Inline_code_emphasis_and_strong_render_with_expected_inline_styles()
    {
        const string markdown = "Here is **bold**, *italic*, and `inline code`.";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        var textBlock = allControls.OfType<SelectableTextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);

        var inlines = textBlock.Inlines ?? new InlineCollection();
        var spans = inlines.OfType<Span>().ToList();

        var boldSpan = spans.FirstOrDefault(s => s.FontWeight == FontWeight.Bold);
        Assert.NotNull(boldSpan);

        var italicSpan = spans.FirstOrDefault(s => s.FontStyle == FontStyle.Italic);
        Assert.NotNull(italicSpan);

        var codeRun = inlines.OfType<Run>().FirstOrDefault(r => r.Text == "inline code");
        Assert.NotNull(codeRun);
        AssertIsTheShippedCodeFace(codeRun.FontFamily);
    }

    [AvaloniaFact]
    public void Image_renders_no_image_control_and_degrades_to_literal_text()
    {
        const string markdown = "![alt text](http://example.com/image.png)";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);

        var textBlock = allControls.OfType<SelectableTextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);

        var inlines = textBlock.Inlines ?? new InlineCollection();
        var textRuns = inlines.OfType<Run>().Select(r => r.Text).ToList();
        Assert.Contains("alt text", textRuns);
    }

    [AvaloniaFact]
    public void Link_renders_visible_text_without_navigation()
    {
        const string markdown = "[click here](http://example.com)";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        var textBlock = allControls.OfType<SelectableTextBlock>().FirstOrDefault();
        Assert.NotNull(textBlock);

        var inlines = textBlock.Inlines ?? new InlineCollection();
        var span = inlines.OfType<Span>().FirstOrDefault();
        Assert.NotNull(span);

        var spanInlines = span.Inlines ?? new InlineCollection();
        var linkRun = spanInlines.OfType<Run>().FirstOrDefault();
        Assert.NotNull(linkRun);
        Assert.Equal("click here", linkRun.Text);
    }

    [AvaloniaFact]
    public void Raw_html_degrades_to_literal_text()
    {
        // The heading discriminates against a no-op "bind raw string to a TextBlock" renderer: real
        // parsing strips the "# " marker (transforms the heading), a no-op leaves it verbatim. So the
        // test proves the HTML is routed through Markdig's AST and degraded, not merely passed through.
        const string markdown = "# Title\n\n<script>alert('xss')</script> and <img src=\"http://evil.com/x.png\">";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);

        var allText = CollectAllText(allControls);
        Assert.Contains("<script>alert('xss')</script>", allText);
        Assert.Contains("<img src=\"http://evil.com/x.png\">", allText);
        // Real parse produced the heading text without its "# " marker; a no-op would keep "# Title".
        Assert.Contains("Title", allText);
        Assert.DoesNotContain("# Title", allText);
    }

    [AvaloniaFact]
    public void Reference_style_image_renders_no_image_control()
    {
        // A different syntax path than the inline image already covered: the image reference is
        // resolved against a link-reference definition, so it exercises the reference branch of
        // LinkInline. 0051 §3 forbids ANY image control regardless of how the image reached the AST.
        const string markdown = "![alt text][ref]\n\n[ref]: http://example.com/image.png";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);
    }

    [AvaloniaFact]
    public void Autolink_is_not_wired_for_navigation_and_issues_no_fetch()
    {
        // The bare pipeline must not silently enable the AutoLinks extension. Even if an autolink
        // slips through as a LinkInline, the renderer must not wire navigation. Assert the URL shows
        // as literal text and no Image/HyperlinkButton reaches the tree.
        const string markdown = "See <http://example.com> for details.";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);
        Assert.DoesNotContain(allControls, c => c is HyperlinkButton);

        var allText = CollectAllText(allControls);
        Assert.Contains("http://example.com", allText);
    }

    [AvaloniaFact]
    public void Inline_html_mid_paragraph_degrades_to_literal_and_loads_no_image()
    {
        // This hits the HtmlInline branch (raw HTML mid-paragraph), distinct from the HtmlBlock path
        // the raw-html test exercises. The <img> tag must survive as literal text, never a control.
        const string markdown = "before <img src=\"http://evil.com/x.png\"> after";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);

        var allText = CollectAllText(allControls);
        Assert.Contains("<img src=\"http://evil.com/x.png\">", allText);
    }

    [AvaloniaFact]
    public void Deeply_nested_blockquotes_degrade_to_literal_via_the_parse_cap()
    {
        // Deep blockquotes hit the parse-cap path in MarkdownRenderer.Render (see its comment for the
        // mechanism). Asserts the observable contract — literal text, no crash — not which branch fired.
        var markdown = string.Concat(Enumerable.Repeat("> ", 500)) + "deep";
        var control = MarkdownRenderer.Render(markdown);

        Assert.IsType<StackPanel>(control);
        Assert.Contains("deep", CollectAllText(GetAllControls(control).ToList()));
    }

    [AvaloniaFact]
    public void Deeply_nested_emphasis_degrades_to_literal_via_the_render_depth_guard()
    {
        // 100 `*` delimiters parse to an AST deeper than the render-depth cap but throw nothing, so
        // this exercises the ExceedsMaxDepth guard rather than the parse-cap catch (see the renderer).
        var markdown = new string('*', 100) + "x" + new string('*', 100);
        var control = MarkdownRenderer.Render(markdown);

        Assert.IsType<StackPanel>(control);
        // Guard degraded to one literal block — the raw delimiters survive as text, no crash.
        Assert.Contains("x", CollectAllText(GetAllControls(control).ToList()));
        Assert.Contains("*", CollectAllText(GetAllControls(control).ToList()));
    }

    private static string CollectAllText(IEnumerable<Control> controls)
    {
        var collected = new List<string>();
        foreach (var tb in controls.OfType<SelectableTextBlock>())
        {
            if (!string.IsNullOrEmpty(tb.Text))
            {
                collected.Add(tb.Text);
            }
            if (tb.Inlines != null)
            {
                collected.Add(CollectInlineText(tb.Inlines));
            }
        }

        return string.Join("\n", collected);
    }

    private static string CollectInlineText(InlineCollection inlines)
    {
        var parts = new List<string>();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run when run.Text != null:
                    parts.Add(run.Text);
                    break;
                case Span span when span.Inlines != null:
                    parts.Add(CollectInlineText(span.Inlines));
                    break;
            }
        }

        return string.Concat(parts);
    }

    // #1125: the renderer previously named a Cascadia/Consolas platform chain here, which resolved
    // to a different face per OS. Name alone would also pass for a same-named font installed on the
    // machine — the asset-URI Key is what proves it is the copy shipped in this repo, the same
    // discrimination ShippedTypefaceTests.AssertResolvesToAShippedAsset makes for the app-wide faces.
    private static void AssertIsTheShippedCodeFace(FontFamily? family)
    {
        Assert.NotNull(family);
        Assert.Equal("JetBrains Mono", family.Name);
        Assert.NotNull(family.Key);
        Assert.Contains("Aer.Ui/Assets/Fonts", family.Key!.Source.ToString());
    }

    private static IEnumerable<Control> GetAllControls(Control parent)
    {
        yield return parent;
        if (parent is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                foreach (var descendant in GetAllControls(child))
                {
                    yield return descendant;
                }
            }
        }
        else if (parent is ContentControl contentControl && contentControl.Content is Control childContent)
        {
            foreach (var descendant in GetAllControls(childContent))
            {
                yield return descendant;
            }
        }
        else if (parent is Border border && border.Child != null)
        {
            foreach (var descendant in GetAllControls(border.Child))
            {
                yield return descendant;
            }
        }
    }
}
