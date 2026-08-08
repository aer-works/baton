using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aer.Ui.Markdown;

internal static class MarkdownRenderer
{
    private static readonly FontFamily MonospaceFont = new("Cascadia Code, Consolas, monospace");

    // #1076 review (MEDIUM), confirmed by measurement (tests/.../MarkdownRendererTests deep-nesting
    // cases): 0051 §1 is that the input is untrusted model output. Markdig caps its own *block*
    // nesting (blockquotes/lists throw past ~128) but does NOT cap *inline emphasis* — 5000 `*`
    // delimiters parse to an AST ~2500 deep. The render walk recurses per level, so without this cap
    // that AST would StackOverflow the desktop process. 48 is far above any real message's nesting.
    private const int MaxRenderDepth = 48;

    public static Control Render(string? markdown)
    {
        var panel = new StackPanel
        {
            Spacing = 8
        };

        if (string.IsNullOrEmpty(markdown))
        {
            return panel;
        }

        var pipeline = new MarkdownPipelineBuilder().Build();

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(markdown, pipeline);
        }
        catch (ArgumentException ex) when (ex.Message.Contains("deeply nested"))
        {
            // Markdig's own block-nesting cap throws this on pathologically deep blockquotes/lists.
            // The `when` filter keeps any unrelated ArgumentException propagating (repo rule: don't
            // silently swallow) — only the known deep-nesting throw degrades to safe literal text.
            return DegradeToLiteral(panel, markdown);
        }

        // Emphasis nesting is NOT capped by Markdig (see MaxRenderDepth), so a parse can succeed and
        // still hand back an AST too deep for the recursive render walk. Guard it before recursing.
        if (ExceedsMaxDepth(document))
        {
            return DegradeToLiteral(panel, markdown);
        }

        foreach (var block in document)
        {
            var control = RenderBlock(block);
            if (control != null)
            {
                panel.Children.Add(control);
            }
        }

        return panel;
    }

    private static Control DegradeToLiteral(StackPanel panel, string markdown)
    {
        panel.Children.Add(new SelectableTextBlock { Text = markdown, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    // Iterative (explicit-stack) depth probe so the guard itself cannot overflow. Walks the block tree
    // and, at each leaf, its inline tree, returning true as soon as any node sits deeper than the cap.
    private static bool ExceedsMaxDepth(MarkdownObject root)
    {
        var stack = new Stack<(MarkdownObject Node, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > MaxRenderDepth)
            {
                return true;
            }

            switch (node)
            {
                case ContainerBlock containerBlock:
                    foreach (var child in containerBlock)
                    {
                        stack.Push((child, depth + 1));
                    }
                    break;
                case LeafBlock leafBlock when leafBlock.Inline is { } inline:
                    stack.Push((inline, depth + 1));
                    break;
                case ContainerInline containerInline:
                    foreach (var child in containerInline)
                    {
                        stack.Push((child, depth + 1));
                    }
                    break;
            }
        }

        return false;
    }

    private static Control? RenderBlock(Block block)
    {
        return block switch
        {
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            HeadingBlock heading => RenderHeading(heading),
            CodeBlock codeBlock => RenderCodeBlock(codeBlock),
            ListBlock listBlock => RenderListBlock(listBlock),
            QuoteBlock quoteBlock => RenderQuoteBlock(quoteBlock),
            ThematicBreakBlock thematicBreak => RenderThematicBreak(thematicBreak),
            HtmlBlock htmlBlock => RenderHtmlBlock(htmlBlock),
            ContainerBlock containerBlock => RenderContainerBlock(containerBlock),
            _ => RenderFallbackBlock(block)
        };
    }

    private static Control RenderParagraph(ParagraphBlock paragraph)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };

        if (paragraph.Inline != null)
        {
            var inlines = tb.Inlines ??= new InlineCollection();
            RenderInlines(paragraph.Inline, inlines);
        }

        return tb;
    }

    private static Control RenderHeading(HeadingBlock heading)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        };

        switch (heading.Level)
        {
            case 1:
                tb.FontSize = 24;
                tb.FontWeight = FontWeight.Bold;
                break;
            case 2:
                tb.FontSize = 20;
                tb.FontWeight = FontWeight.Bold;
                break;
            case 3:
                tb.FontSize = 18;
                tb.FontWeight = FontWeight.SemiBold;
                break;
            case 4:
                tb.FontSize = 16;
                tb.FontWeight = FontWeight.SemiBold;
                break;
            case 5:
                tb.FontSize = 14;
                tb.FontWeight = FontWeight.Bold;
                break;
            default:
                tb.FontSize = 13;
                tb.FontWeight = FontWeight.Bold;
                break;
        }

        if (heading.Inline != null)
        {
            var inlines = tb.Inlines ??= new InlineCollection();
            RenderInlines(heading.Inline, inlines);
        }

        return tb;
    }

    private static Control RenderCodeBlock(CodeBlock codeBlock)
    {
        var lines = new List<string>();
        for (int i = 0; i < codeBlock.Lines.Count; i++)
        {
            lines.Add(codeBlock.Lines.Lines[i].ToString());
        }
        var codeText = string.Join("\n", lines);

        var tb = new SelectableTextBlock
        {
            Text = codeText,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = MonospaceFont
        };

        // #1076 review (HIGH): NoWrap without a horizontal scroller clips wide code/diff lines — the
        // exact content 0051 exists to render — with no way to reach the rest. Give the block its own
        // horizontal ScrollViewer (bounded to the block, not the transcript), matching the
        // HorizontalScrollBarVisibility="Auto" convention AuthorView/RoomView already use.
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = tb
        };

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 4, 0, 8),
            Child = scroller
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Color.SurfaceSubtle"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("Color.Border"));

        return border;
    }

    private static Control RenderListBlock(ListBlock listBlock)
    {
        var listPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 2, 0, 4)
        };

        int index = 0;
        int start = 1;
        if (listBlock.IsOrdered && listBlock.OrderedStart != null && int.TryParse(listBlock.OrderedStart, out var parsedStart))
        {
            start = parsedStart;
        }

        foreach (var itemNode in listBlock)
        {
            if (itemNode is ListItemBlock listItem)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Margin = new Thickness(0, 1)
                };

                string markerText = listBlock.IsOrdered ? $"{start + index}." : "•";

                var marker = new TextBlock
                {
                    Text = markerText,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(marker, 0);
                row.Children.Add(marker);

                var itemPanel = new StackPanel { Spacing = 4 };
                foreach (var child in listItem)
                {
                    var childControl = RenderBlock(child);
                    if (childControl != null)
                    {
                        itemPanel.Children.Add(childControl);
                    }
                }

                Grid.SetColumn(itemPanel, 1);
                row.Children.Add(itemPanel);

                listPanel.Children.Add(row);
                index++;
            }
        }

        return listPanel;
    }

    private static Control RenderQuoteBlock(QuoteBlock quoteBlock)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var child in quoteBlock)
        {
            var childControl = RenderBlock(child);
            if (childControl != null)
            {
                panel.Children.Add(childControl);
            }
        }

        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 4, 0, 4),
            Margin = new Thickness(0, 4, 0, 4),
            Child = panel
        };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("Color.Accent"));
        return border;
    }

    private static Control RenderThematicBreak(ThematicBreakBlock thematicBreak)
    {
        var border = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Color.Border"));
        return border;
    }

    private static Control RenderHtmlBlock(HtmlBlock htmlBlock)
    {
        var lines = new List<string>();
        for (int i = 0; i < htmlBlock.Lines.Count; i++)
        {
            lines.Add(htmlBlock.Lines.Lines[i].ToString());
        }
        var rawText = string.Join("\n", lines);

        return new SelectableTextBlock
        {
            Text = rawText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
    }

    private static Control RenderContainerBlock(ContainerBlock containerBlock)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var child in containerBlock)
        {
            var control = RenderBlock(child);
            if (control != null)
            {
                panel.Children.Add(control);
            }
        }
        return panel;
    }

    private static Control? RenderFallbackBlock(Block block)
    {
        var text = GetBlockLiteralText(block);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static void RenderInlines(ContainerInline containerInline, InlineCollection inlines)
    {
        foreach (var inline in containerInline)
        {
            RenderInline(inline, inlines);
        }
    }

    private static void RenderInline(Markdig.Syntax.Inlines.Inline inline, InlineCollection inlines)
    {
        switch (inline)
        {
            case LiteralInline literalText:
                inlines.Add(new Run(literalText.Content.ToString()));
                break;

            case LineBreakInline:
                inlines.Add(new LineBreak());
                break;

            case EmphasisInline emphasis:
                var span = new Span();
                // CommonMark pairs a run of 3 delimiters as 2+1 (strong wrapping em), so a single
                // EmphasisInline carries either 1 (italic) or 2 (bold); ***x*** is two nested nodes,
                // and the italic inherits the outer bold via Span inheritance (#1076 review #6).
                if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeight.Bold;
                }
                else
                {
                    span.FontStyle = FontStyle.Italic;
                }
                var spanInlines = span.Inlines ??= new InlineCollection();
                RenderInlines(emphasis, spanInlines);
                inlines.Add(span);
                break;

            case CodeInline codeInline:
                var codeRun = new Run(codeInline.Content)
                {
                    FontFamily = MonospaceFont
                };
                inlines.Add(codeRun);
                break;

            case LinkInline linkInline:
                if (linkInline.IsImage)
                {
                    // SECURITY: Never render an Image control or fetch remote images.
                    // Degrade to literal placeholder text (e.g. alt text or "[image]").
                    var altText = GetInlineText(linkInline);
                    var displayText = !string.IsNullOrEmpty(altText) ? altText : "[image]";
                    inlines.Add(new Run(displayText));
                }
                else
                {
                    // SECURITY: Style as a link (accent foreground, underline), but no navigation/fetch.
                    var linkSpan = new Span
                    {
                        TextDecorations = TextDecorations.Underline
                    };
                    linkSpan.Bind(TextElement.ForegroundProperty, linkSpan.GetResourceObservable("Color.Accent"));

                    var linkInlines = linkSpan.Inlines ??= new InlineCollection();
                    if (linkInline.FirstChild != null)
                    {
                        RenderInlines(linkInline, linkInlines);
                    }
                    else if (!string.IsNullOrEmpty(linkInline.Url))
                    {
                        linkInlines.Add(new Run(linkInline.Url));
                    }
                    inlines.Add(linkSpan);
                }
                break;

            case HtmlInline htmlInline:
                // SECURITY: Degrade HTML inline to literal text
                inlines.Add(new Run(htmlInline.Tag.ToString()));
                break;

            case HtmlEntityInline htmlEntityInline:
                // SECURITY: Degrade HTML entity inline to literal text
                inlines.Add(new Run(htmlEntityInline.Transcoded.ToString()));
                break;

            case ContainerInline container:
                var containerSpan = new Span();
                var containerInlines = containerSpan.Inlines ??= new InlineCollection();
                RenderInlines(container, containerInlines);
                inlines.Add(containerSpan);
                break;

            default:
                var text = GetInlineText(inline);
                if (!string.IsNullOrEmpty(text))
                {
                    inlines.Add(new Run(text));
                }
                break;
        }
    }

    private static string GetInlineText(Markdig.Syntax.Inlines.Inline inline)
    {
        return inline switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            HtmlInline html => html.Tag.ToString(),
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            ContainerInline container => string.Concat(container.Select(GetInlineText)),
            _ => inline.ToString() ?? string.Empty
        };
    }

    private static string GetBlockLiteralText(Block block)
    {
        if (block is LeafBlock leafBlock && leafBlock.Lines.Count > 0)
        {
            var lines = new List<string>();
            for (int i = 0; i < leafBlock.Lines.Count; i++)
            {
                lines.Add(leafBlock.Lines.Lines[i].ToString());
            }
            return string.Join("\n", lines);
        }
        return string.Empty;
    }
}
