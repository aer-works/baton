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
                if (IsOffendingLine(lines[i]))
                {
                    offenders.Add($"{Path.GetFileName(filePath)}:{i + 1}: {lines[i].Trim()}");
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
    /// must fire, and the fixed line must not — a scan that fails both directions of that polarity
    /// is a statement about the harness, not the sources. Every fixture goes through
    /// <see cref="IsOffendingLine"/>, the same path the real scan walks, so the comment-stripping
    /// half is exercised too — a polarity check against the bare regexes would certify a scan whose
    /// stripping had gone blind.
    /// </summary>
    [Fact]
    public void The_scan_discriminates_the_removed_literal_from_its_replacement()
    {
        Assert.True(IsOffendingLine(@"private static readonly FontFamily MonospaceFont = new(""Cascadia Code, Consolas, monospace"");"));
        Assert.False(IsOffendingLine("private static readonly FontFamily MonospaceFont = new(AerFonts.Mono);"));

        Assert.True(IsOffendingLine("Foreground = new SolidColorBrush(Color.Parse(\"#FF0000\"))"));
        Assert.True(IsOffendingLine("Foreground = Brushes.Red"));
        Assert.True(IsOffendingLine("Background = new SolidColorBrush(Colors.Black)"));
        Assert.False(IsOffendingLine("border.Bind(Border.BackgroundProperty, border.GetResourceObservable(\"Color.SurfaceSubtle\"));"));

        // The stripping half of the pipeline, both polarities: prose about the old literal in a
        // comment must not fire, and a live literal must still fire with a trailing comment present.
        Assert.False(IsOffendingLine(@"    // previously new FontFamily(""Cascadia Code"") — see #1125"));
        Assert.True(IsOffendingLine(@"    FontFamily = new FontFamily(""Cascadia Code"") // migrated later"));
    }

    private static bool IsOffendingLine(string rawLine)
    {
        // Strip line comments so prose about fonts cannot fire the scan; a literal hiding
        // after `//` is not compiled and is not drift.
        var code = StripLineComment(rawLine);
        return FontLiteral.IsMatch(code) || ColorLiteral.IsMatch(code);
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
            if (File.Exists(Path.Combine(dir.FullName, "AerFlow.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (AerFlow.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
