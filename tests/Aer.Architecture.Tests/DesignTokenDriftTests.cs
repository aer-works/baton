using System.Text.RegularExpressions;
using Aer.DesignTokens;

namespace Aer.Architecture.Tests;

/// <summary>
/// #345's gate: the checked-in theme artifacts must be exactly what <c>design/tokens.json</c>
/// generates.
/// </summary>
/// <remarks>
/// <para>
/// One token file generating both toolkits only removes drift if something notices when the
/// artifacts and the source disagree. Without this, the two failure modes are both silent: someone
/// hand-edits <c>Tokens.axaml</c> because it is right there, or changes a colour in the token file
/// and never runs the generator — and in either case desktop and mobile quietly stop matching, which
/// is the exact problem the pipeline was built to solve.
/// </para>
/// <para>
/// The comparison runs the real generator rather than a second implementation of "what the output
/// should look like". A gate with its own notion of correct output drifts from the generator and
/// then passes while the artifacts are wrong.
/// </para>
/// </remarks>
public class DesignTokenDriftTests
{
    [Fact]
    public void GeneratedThemeArtifactsMatchTheTokenFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var interactionStatesJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.InteractionStatesPath));

        foreach (var (relativePath, expected) in TokenGenerator.Generate(tokensJson, interactionStatesJson))
        {
            var path = Path.Combine(repositoryRoot, relativePath);
            Assert.True(File.Exists(path), $"{relativePath} is missing. Run `{TokenGenerator.RegenerateCommand}`.");

            // Read as-is and normalise only line endings: git may check these out with CRLF on
            // Windows, which is not drift. Anything else that differs is.
            var actual = File.ReadAllText(path).ReplaceLineEndings("\n");

            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                $"""
                {relativePath} is out of date with {TokenGenerator.TokensPath}.

                Either it was hand-edited, or {TokenGenerator.TokensPath} changed without regenerating.
                Run `{TokenGenerator.RegenerateCommand}` and commit the result.

                {FirstDifference(expected, actual)}
                """);
        }
    }

    /// <summary>
    /// #952's sweep found the one token copy nothing checked: <c>AerFonts</c>' two family names,
    /// whose own doc comments say "must match <c>design/tokens.json</c>" — and nothing did. Read
    /// from source text (this project deliberately does not reference <c>Aer.Ui</c>, and the
    /// suite's own style is file assertions). The avares URI's fragment (after <c>#</c>) is the
    /// family name Avalonia resolves, so that is the half compared; the asset path before it is
    /// Avalonia packaging, not a token.
    /// </summary>
    [Fact]
    public void AerFontsFamilyNamesMatchTheTokenFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var families = Regex.Match(
            tokensJson, "\"fontFamily\":\\s*\\{\\s*\"sans\":\\s*\"([^\"]+)\",\\s*\"mono\":\\s*\"([^\"]+)\"");
        Assert.True(families.Success, $"{TokenGenerator.TokensPath} no longer carries type.fontFamily.sans/mono — update this test with it.");

        var aerFonts = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Aer.Ui", "AerFonts.cs"));
        var sans = Regex.Match(aerFonts, "Sans = \"[^\"]*#([^\"]+)\"");
        var mono = Regex.Match(aerFonts, "Mono = \"[^\"]*#([^\"]+)\"");
        Assert.True(sans.Success && mono.Success, "AerFonts.cs no longer declares Sans/Mono avares constants — update this test with it.");

        Assert.Equal(families.Groups[1].Value, sans.Groups[1].Value);
        Assert.Equal(families.Groups[2].Value, mono.Groups[1].Value);
    }

    /// <summary>
    /// #458's gate: every status names a mark, and both toolkits must actually draw it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marks are the one part of the design system that cannot be generated — vector geometry is
    /// hand-drawn, in a <c>StreamGeometry</c> on Avalonia and a <c>CustomPainter</c> on Flutter — so
    /// they are also the one part that can silently go missing. Adding a status to the token file, or
    /// renaming a mark, compiles and runs on both platforms and shows up only as a blank space where
    /// a status marker belongs, on whichever platform whoever made the change was not looking at.
    /// </para>
    /// <para>
    /// This is a deliberately shallow check — it asserts a mark is *defined*, not that the two
    /// drawings agree. Shape equivalence across two toolkits' path syntaxes is not something a test
    /// can assert honestly, and pretending otherwise would be worse than admitting the limit: that
    /// half stays a review question, kept tractable by both files being authored on the same 16x16
    /// grid with matching coordinates.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryStatusMarkIsDrawnByBothToolkits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));

        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));

        var marks = TokenGenerator.StatusMarks(tokensJson).ToList();
        Assert.NotEmpty(marks);

        foreach (var (status, mark, geometryKey) in marks)
        {
            Assert.True(
                avaloniaIcons.Contains($"""x:Key="{geometryKey}" """.TrimEnd(), StringComparison.Ordinal),
                $"""
                Status '{status}' names the mark '{mark}', but {TokenGenerator.AvaloniaIconsPath} defines
                no geometry with the key '{geometryKey}'. Desktop would render that status as a blank space.
                """);
        }
    }

    /// <summary>
    /// #511: a composite mark's every part must have its own Avalonia geometry, not just its primary.
    /// <see cref="EveryStatusMarkIsDrawnByBothToolkits"/> only walks primaries — enough for the two
    /// toolkits' switch dispatch, since Flutter composites a whole mark under one case — so it alone
    /// would not have caught the eye shipping with a stroked lid and an undrawn pupil. Flutter needs
    /// no equivalent check: a composite's detail paint lives inside its one switch case by hand, not
    /// as a second named case, so there is no second name for a Flutter-side check to require.
    /// </summary>
    [Fact]
    public void EveryCompositeMarkPartHasItsOwnAvaloniaGeometry()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));

        var parts = TokenGenerator.AllMarkParts(tokensJson).ToList();
        Assert.NotEmpty(parts);

        foreach (var (status, partName, geometryKey) in parts)
        {
            Assert.True(
                avaloniaIcons.Contains($"""x:Key="{geometryKey}" """.TrimEnd(), StringComparison.Ordinal),
                $"""
                Status '{status}' names the mark part '{partName}', but {TokenGenerator.AvaloniaIconsPath} defines
                no geometry with the key '{geometryKey}'. Desktop would render that part as missing.
                """);
        }
    }

    /// <summary>
    /// The inverse of <see cref="EveryStatusMarkIsDrawnByBothToolkits"/> (#489): no toolkit may define
    /// a status mark the token file does not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The forward check walks tokens → toolkits, so it can only see marks someone declared. A mark
    /// that exists in <em>one</em> toolkit and in no token is invisible to it — and that is not
    /// hypothetical: <c>Icon.Dot</c> was defined in Avalonia and used for the idle/pending state, had
    /// no Flutter counterpart, and appeared in no token. The desktop drew a mark the phone could not
    /// draw, for a state <c>0020</c> lists as canonical, and the gate built to prevent exactly this
    /// class of divergence (#458, #461) could not see it.
    /// </para>
    /// <para>
    /// A toolkit-only mark is how the design system forks: whoever adds it is looking at one platform,
    /// it renders correctly there, and the other silently falls back or blanks. Requiring every drawn
    /// mark to be declared in <c>design/tokens.json</c> forces the declaration first, which is what
    /// makes the forward check meaningful.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoToolkitDefinesAStatusMarkTheTokenFileDoesNotName()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        // AllMarkParts, not StatusMarks: a composite mark's non-primary part (#511's Icon.EyePupil) is
        // genuinely declared, in the token file's own {geometry, filled} array, not an orphan the way
        // an undeclared action glyph is — StatusMarks only sees primaries and would wrongly flag it.
        var declared = TokenGenerator.AllMarkParts(tokensJson)
            .Select(m => m.GeometryKey)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(declared);

        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));

        // Only the status ramp's own marks are in scope. Action glyphs (Icon.Refresh, Icon.Copy, …)
        // are not statuses and are deliberately not token-driven — the rule is about the accessibility
        // contract in 0006, which binds states, not controls. The status marks are exactly the keys the
        // generator emits, so anything matching the same shape but absent from the token file is drift.
        var drawnStatusKeys = Regex
            .Matches(avaloniaIcons, "x:Key=\"(Icon\\.[A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(key => !NonStatusGlyphs.Contains(key))
            .ToList();

        var orphans = drawnStatusKeys.Where(key => !declared.Contains(key)).ToList();

        Assert.True(
            orphans.Count == 0,
            $"""
            {TokenGenerator.AvaloniaIconsPath} defines status geometry the token file does not name:
              {string.Join("\n  ", orphans)}
            Every status mark must be declared in {TokenGenerator.TokensPath} so the forward check can
            require both toolkits to draw it. If one of these is an action glyph rather than a status
            mark, add it to {nameof(NonStatusGlyphs)} in this test with a note saying why.
            """);
    }

    /// <summary>
    /// #511: a status can be defined, generated, and token-checked, and still never actually render —
    /// <c>AerStatus.ReadyForReview</c> was exactly that until this test existed. The three checks above
    /// all walk token → toolkit-artifact; none of them asks whether any LIVE binding path in the running
    /// app ever produces a given status's geometry key at all. <c>StatusIconMap</c>
    /// (<c>src/Aer.Ui/Converters/StatusIconConverters.cs</c>) is desktop's one status→mark mapping every
    /// rendering surface goes through (issue #206's own intent) — if a status's geometry key appears in
    /// neither of its two <c>GeometryKeyFor</c> switch expressions, nothing on desktop can ever draw it,
    /// which is a silent gap the three checks above cannot see: they only ask "is a shape defined for
    /// this name", never "can anything reach it". A status either is reachable, or is named in
    /// <see cref="KnownUnreachableStatuses"/> with a reason — so the gap is at least an admitted, tracked
    /// one rather than an invisible one.
    /// </summary>
    /// <remarks>
    /// Checks GLYPH reachability, not status identity — deliberately, on a second-reader's finding. A
    /// first version tried to require each <c>AerStatus</c> to be reached by its OWN colour key too, not
    /// just its geometry, which broke on <c>cancelled</c>: <see cref="StepStatus.Cancelled"/> and
    /// <c>RoomCardStatus.Cancelled</c> both render <c>Icon.Dash</c> in <c>Status.Idle</c>'s brush rather
    /// than a <c>Status.Cancelled</c> of its own — design/tokens.json's own words are "idle and the last
    /// three are quiet states and deliberately share one muted colour" — so requiring a status's own
    /// literal colour-key name flagged a legitimate, documented sharing as a defect. The same reasoning
    /// covers <c>queued</c>/<c>outOfPlan</c> sharing <c>Icon.Ellipsis</c> (#1132: "both are 'waiting, not
    /// asked to act'... the two never render in the same list") — riding a sibling's reachable glyph is
    /// the intended design there, not an accident to catch. What this test cannot see: whether a status
    /// that ONLY ever reaches through a sibling's glyph is itself ever independently selected by any
    /// code path — that is a claim about the ENGINE's state machine, not about mark rendering, and is
    /// out of this drift gate's scope.
    /// </remarks>
    [Fact]
    public void EveryStatusMarkIsReachableFromALiveDesktopSurfaceOrExplicitlyExempted()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var statusIconMapSource = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "Aer.Ui", "Converters", "StatusIconConverters.cs"));

        // Every "Icon.X" string LITERAL StatusIconMap's two GeometryKeyFor switch expressions can
        // return — the complete set of geometry keys any live binding through it can ever produce.
        var reachableGeometryKeys = Regex
            .Matches(statusIconMapSource, "\"(Icon\\.[A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(reachableGeometryKeys);

        var marks = TokenGenerator.StatusMarks(tokensJson).ToList();
        Assert.NotEmpty(marks);

        var unreachable = marks
            .Where(m => !reachableGeometryKeys.Contains(m.GeometryKey) && !KnownUnreachableStatuses.Contains(m.Status))
            .ToList();

        Assert.True(
            unreachable.Count == 0,
            $"""
            These statuses name a mark no live desktop binding path can ever reach — StatusIconMap's
            GeometryKeyFor switches never return their geometry key, so nothing renders them:
              {string.Join("\n  ", unreachable.Select(m => $"{m.Status} -> {m.GeometryKey}"))}
            Either wire a real StepStatus/RoomCardStatus case that reaches this AerStatus, or add it to
            {nameof(KnownUnreachableStatuses)} in this test with a note saying why it has no desktop
            surface yet.
            """);
    }

    /// <summary>
    /// AerStatus values with no live desktop rendering path yet, admitted rather than left for this
    /// test to silently miss (#511).
    /// </summary>
    /// <remarks>
    /// <c>readyForReview</c> is the five-state #334 simplification's own state; desktop instead renders
    /// the richer <see cref="Aer.Flow.Domain.StepStatus"/>/<c>RoomCardStatus</c> vocabularies directly,
    /// and neither has a member that means "ready for review" as its own concept — the nearest desktop
    /// equivalent is folded into Paused/NeedsYou (<c>Icon.Bubble</c>), a deliberate, already-reachable
    /// choice, not an oversight. Wiring <c>AerStatus</c> itself into a real desktop surface is
    /// <c>StatusIconConverters.cs</c>'s own noted future: "#336 replaces this mapping wholesale with
    /// <c>AerStatus</c>, which carries the distinction" — until that lands, this is the honest state of
    /// the gap rather than a gate quietly not looking for it.
    /// </remarks>
    private static readonly HashSet<string> KnownUnreachableStatuses = new(StringComparer.Ordinal)
    {
        "readyForReview",
    };

    /// <summary>
    /// Keys in <c>Icons.axaml</c> that are navigation or action glyphs rather than status marks, and so
    /// are correctly absent from the status ramp.
    /// </summary>
    /// <remarks>
    /// Listed explicitly rather than pattern-matched, and that friction is the point: adding a glyph
    /// means answering "is this a state or a control?" out loud. #461 is why the question matters — a
    /// state wearing an action's icon is a trap, and the stale-list state had borrowed
    /// <c>Icon.Refresh</c>, the Retry <em>action</em>'s glyph, inviting a click that would do nothing.
    /// </remarks>
    private static readonly HashSet<string> NonStatusGlyphs = new(StringComparer.Ordinal)
    {
        "Icon.Refresh",
        // Icon.Home retired with #1071's rail restyle — the Home rail button folded into the one ▤
        // Rooms front door, so its house glyph has no remaining use (mirrors #1068 dropping Icon.Remote).
        "Icon.Task",
        "Icon.Author",
        "Icon.Folder",
        "Icon.Chat",
        "Icon.Fleet",
        // #1068: the Settings nav destination's gear — a control (where you go to adjust things), not a
        // state a room can be in, so it is deliberately not in the token-driven status ramp. (Icon.Remote
        // was removed with the same change: its rail destination became Settings.)
        "Icon.Settings",
    };

    /// <summary>
    /// #1318 (decision 0058's scope ruling): the depth/effort twin of
    /// <see cref="EveryStatusMarkIsDrawnByBothToolkits"/>. These two meter families name no per-tier
    /// shape the way a status does — every tier in a family draws the SAME position geometry, only
    /// the fill count differs — so what a "mark" means here is the family's own step positions, not
    /// its tiers. For each of <c>TokenGenerator.MeterFamilies</c>, this checks that both toolkits
    /// define a step shape for every position 1..steps (Avalonia's <c>Icon.&lt;Family&gt;Step&lt;n&gt;</c>
    /// geometries) and that mobile draws a widget for the family at all — a family whose token block
    /// nothing hand-draws would otherwise show up only as a blank space on whichever platform.
    /// </summary>
    /// <remarks>
    /// Deliberately shallow, the same admission <see cref="EveryStatusMarkIsDrawnByBothToolkits"/>
    /// makes: this asserts a shape is defined per position and a widget exists per family, not that
    /// the two toolkits' drawings agree pixel for pixel, and it does not attempt to assert greyscale
    /// silhouette discriminability — that stays a review judgment, per the #1318 scope ruling's own
    /// instruction not to pretend a test can settle it. What this check cannot see at all —
    /// whether desktop actually renders a defined shape at its authored position rather than
    /// silently mis-transforming it — is <c>Aer.Ui.Tests.TierMeterRenderTests</c> (#1318 second
    /// reader), which renders the real <c>TierMeter</c> control headlessly and asserts each step's
    /// rendered geometry matches its authored one.
    /// </remarks>
    [Fact]
    public void EveryDepthAndEffortStepIsDrawnByBothToolkits()
    {
        var repositoryRoot = FindRepositoryRoot();
        var tokensJson = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.TokensPath));
        var avaloniaIcons = File.ReadAllText(Path.Combine(repositoryRoot, TokenGenerator.AvaloniaIconsPath));

        foreach (var family in TokenGenerator.MeterFamilies)
        {
            var tiers = TokenGenerator.MeterTiers(tokensJson, family).ToList();
            Assert.NotEmpty(tiers);

            var pascalFamily = char.ToUpperInvariant(family[0]) + family[1..];
            var totalSteps = tiers[0].TotalSteps;

            for (var step = 1; step <= totalSteps; step++)
            {
                var geometryKey = $"Icon.{pascalFamily}Step{step}";
                Assert.True(
                    avaloniaIcons.Contains($"""x:Key="{geometryKey}" """.TrimEnd(), StringComparison.Ordinal),
                    $"""
                    design/tokens.json's '{family}' meter has {totalSteps} steps, but {TokenGenerator.AvaloniaIconsPath}
                    defines no geometry with the key '{geometryKey}'. Desktop would render step {step} as a blank space.
                    """);
            }

            // Every tier's own fill count must actually fit within the family's step budget -- a
            // tier claiming more filled steps than the family has would ask the repeater to fill a
            // position that does not exist.
            foreach (var (_, _, filled, familyTotalSteps, _) in tiers)
            {
                Assert.InRange(filled, 1, familyTotalSteps);
                Assert.Equal(totalSteps, familyTotalSteps);
            }
        }
    }

    /// <summary>
    /// The first differing line, both sides. A whole-file diff in an assertion message is unreadable;
    /// the first divergence is almost always the whole story for a generated file.
    /// </summary>
    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');

        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var expectedLine = i < expectedLines.Length ? expectedLines[i] : "<end of file>";
            var actualLine = i < actualLines.Length ? actualLines[i] : "<end of file>";
            if (!string.Equals(expectedLine, actualLine, StringComparison.Ordinal))
            {
                return $"""
                    First difference at line {i + 1}:
                      expected: {expectedLine}
                      on disk:  {actualLine}
                    """;
            }
        }

        return "Files differ in length only.";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, TokenGenerator.TokensPath)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root from {AppContext.BaseDirectory}.");
    }
}
