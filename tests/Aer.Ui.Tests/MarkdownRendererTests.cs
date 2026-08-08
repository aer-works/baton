using System.Collections.Generic;
using System.Linq;
using Aer.Ui.Markdown;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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

        var selectableTextBlock = border.Child as SelectableTextBlock;
        Assert.NotNull(selectableTextBlock);
        Assert.Equal("var x = 42;", selectableTextBlock.Text?.TrimEnd());
        Assert.NotNull(selectableTextBlock.FontFamily);
        Assert.True(
            selectableTextBlock.FontFamily.Name.Contains("monospace") ||
            selectableTextBlock.FontFamily.Name.Contains("Cascadia") ||
            selectableTextBlock.FontFamily.Name.Contains("Consolas"),
            $"Expected monospace font family, got: {selectableTextBlock.FontFamily.Name}"
        );
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
        Assert.NotNull(codeRun.FontFamily);
        Assert.True(
            codeRun.FontFamily.Name.Contains("monospace") ||
            codeRun.FontFamily.Name.Contains("Cascadia") ||
            codeRun.FontFamily.Name.Contains("Consolas"),
            $"Expected monospace font family on inline code, got: {codeRun.FontFamily.Name}"
        );
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
        const string markdown = "<script>alert('xss')</script> and <img src=\"http://evil.com/x.png\">";
        var control = MarkdownRenderer.Render(markdown);

        var allControls = GetAllControls(control).ToList();
        Assert.DoesNotContain(allControls, c => c is Image);

        var textBlocks = allControls.OfType<SelectableTextBlock>().ToList();
        Assert.NotEmpty(textBlocks);

        var collectedTexts = new List<string>();
        foreach (var tb in textBlocks)
        {
            if (!string.IsNullOrEmpty(tb.Text))
            {
                collectedTexts.Add(tb.Text);
            }
            if (tb.Inlines != null)
            {
                collectedTexts.Add(string.Join("", tb.Inlines.OfType<Run>().Select(r => r.Text)));
            }
        }

        var allText = string.Join("\n", collectedTexts);
        Assert.Contains("<script>alert('xss')</script>", allText);
        Assert.Contains("<img src=\"http://evil.com/x.png\">", allText);
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
