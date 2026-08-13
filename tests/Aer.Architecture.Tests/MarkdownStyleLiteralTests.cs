using System.Text.RegularExpressions;

namespace Aer.Architecture.Tests;

/// <summary>
/// #1125: <c>MarkdownRenderer</c> hardcoded a <c>Cascadia Code, Consolas, monospace</c> chain while
/// the app ships JetBrains Mono behind <c>AerFonts.Mono</c> — so code type silently diverged from
/// every other mono surface on any non-Windows machine. The behavioural half is asserted by
/// <c>MarkdownRendererTests</c> (the rendered face resolves to the shipped asset); this is the
/// drift half, failing the moment a font or colour literal reappears anywhere under
/// <c>src/Aer.Ui/Markdown/</c> instead of coming from <c>AerFonts</c> / the theme resources.
/// </summary>
public class MarkdownStyleLiteralTests
{
    // A FontFamily mention sharing a line with a string literal — new FontFamily("..."), the
    // target-typed new("...") on a FontFamily member, or an attribute-style assignment. Resource
    // lookups and AerFonts constants carry no quote on the FontFamily line, so they pass.
    private static readonly Regex FontLiteral = new(@"FontFamily[^""\r\n]*""", RegexOptions.Singleline);

    // Colour literals in any of the three spellings Avalonia accepts in code. `Colors\.` (plural)
    // deliberately does not match the renderer's legitimate resource keys ("Color.SurfaceSubtle"
    // etc.), which are names of theme tokens, not colour values.
    private static readonly Regex ColorLiteral = new(@"Colors\.\w|Brushes\.\w|Color\.Parse|#[0-9A-Fa-f]{6}", RegexOptions.Singleline);

    [Fact]
    public void Markdown_rendering_sources_carry_no_font_or_color_literals()
    {
        var markdownDir = Path.Combine(RepoRoot(), "src", "Aer.Ui", "Markdown");
        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(markdownDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(filePath);
            for (var i = 0; i < lines.Length; i++)
            {
                // Strip line comments so prose about fonts cannot fire the scan; a literal hiding
                // after `//` is not compiled and is not drift.
                var code = StripLineComment(lines[i]);
                if (FontLiteral.IsMatch(code) || ColorLiteral.IsMatch(code))
                {
                    offenders.Add($"{Path.GetFileName(filePath)}:{i + 1}: {code.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Font/colour literal(s) in src/Aer.Ui/Markdown/ — take faces from AerFonts and colours " +
            "from the theme resources (#1125):\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// The control arm: the exact line this test exists to keep out (the pre-#1125 renderer line)
    /// must match, and the fixed line must not — a scan that fails both directions of that polarity
    /// is a statement about the harness, not the sources.
    /// </summary>
    [Fact]
    public void The_scan_discriminates_the_removed_literal_from_its_replacement()
    {
        Assert.Matches(FontLiteral, @"private static readonly FontFamily MonospaceFont = new(""Cascadia Code, Consolas, monospace"");");
        Assert.DoesNotMatch(FontLiteral, "private static readonly FontFamily MonospaceFont = new(AerFonts.Mono);");

        Assert.Matches(ColorLiteral, "Foreground = new SolidColorBrush(Color.Parse(\"#FF0000\"))");
        Assert.Matches(ColorLiteral, "Foreground = Brushes.Red");
        Assert.Matches(ColorLiteral, "Background = new SolidColorBrush(Colors.Black)");
        Assert.DoesNotMatch(ColorLiteral, "border.Bind(Border.BackgroundProperty, border.GetResourceObservable(\"Color.SurfaceSubtle\"));");
    }

    private static string StripLineComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "plan.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (docs/plan.md) by walking up from " + AppContext.BaseDirectory);
    }
}
