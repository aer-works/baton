using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Aer.DesignTokens;

/// <summary>
/// Renders <c>design/tokens.json</c> into each toolkit's theme resources (#345).
/// </summary>
/// <remarks>
/// <para>
/// Desktop is Avalonia and mobile is Flutter, and they share no styling primitive — so "one brand
/// across both" maintained by hand is two sources of truth that drift on the first change, the same
/// failure mode as the vocabulary map (#315) expressed in pixels. Generating both from one file makes
/// the drift a build failure instead of something a reviewer has to notice.
/// </para>
/// <para>
/// <b>Pure by design.</b> Generation is a string function of the parsed token file — no clock, no
/// environment, no filesystem reads beyond the input. That is what lets the CI gate regenerate in
/// memory and compare against the checked-in artifacts: a generator that varied with anything else
/// would make the gate flap and get disabled, which is exactly how a stale artifact survives.
/// </para>
/// <para>
/// <b>Emitted, not interpreted.</b> This renders whatever the token file says; it does not decide
/// design. If a value looks wrong, the fix is in <c>design/tokens.json</c>.
/// </para>
/// </remarks>
public static class TokenGenerator
{
    /// <summary>Every generated artifact, keyed by repo-relative path.</summary>
    public static IReadOnlyDictionary<string, string> Generate(string tokensJson, string interactionStatesJson)
    {
        using var document = JsonDocument.Parse(tokensJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
        });
        using var statesDocument = JsonDocument.Parse(interactionStatesJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
        });

        var root = document.RootElement;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AvaloniaOutputPath] = GenerateAvalonia(root),
            [FlutterOutputPath] = GenerateFlutter(root),
            [UiCoreOutputPath] = GenerateUiCore(root),
            [InteractionStatesOutputPath] = GenerateInteractionStates(statesDocument.RootElement),
        };
    }

    public const string TokensPath = "design/tokens.json";

    /// <summary>
    /// The interaction-state register (#616) — a second population from the same design directory,
    /// deliberately a separate file from <see cref="TokensPath"/>: /status is the room-lifecycle
    /// vocabulary, this is the screen-situation inventory, and merging them would let checks
    /// quantify over meaningless combinations.
    /// </summary>
    public const string InteractionStatesPath = "design/interaction-states.json";

    /// <summary>
    /// The interaction states as a C# type (#616) — however many the register holds, which is the
    /// point of generating it rather than counting it here — same pattern and same reasoning as
    /// <see cref="UiCoreOutputPath"/>: a surface can only be forced to handle every state when
    /// "every state" is a closed type the compiler can quantify over, and generating that type
    /// means the register and the code cannot disagree about what the states even are.
    /// </summary>
    public const string InteractionStatesOutputPath = "src/Aer.Ui.Core/GeneratedInteractionStates.cs";
    public const string AvaloniaOutputPath = "src/Aer.Ui/Theme/GeneratedTokens.axaml";
    public const string FlutterOutputPath = "src/Aer.Mobile/lib/theme/tokens.dart";

    /// <summary>
    /// The five states as a C# type (#458). Flutter has had a generated <c>AerStatus</c> enum since
    /// #345 while the desktop side had nothing — its converters still keyed on the pre-#334
    /// <c>StepStatus</c>/<c>RoomCardStatus</c> vocabularies, which is how <c>readyForReview</c> ended
    /// up with no mark at all and <c>needsInput</c> ended up drawing the same dot as idle. Generating
    /// it means the two toolkits cannot disagree about what the states even are.
    /// </summary>
    public const string UiCoreOutputPath = "src/Aer.Ui.Core/GeneratedStatus.cs";

    /// <summary>
    /// Where each toolkit draws the status marks. These files are hand-written, not generated —
    /// vector geometry is not something this generator invents — which is exactly why the drift gate
    /// has to check them: a status can name a mark that neither file draws, and the only symptom
    /// would be a blank space where a status marker belongs.
    /// </summary>
    public const string AvaloniaIconsPath = "src/Aer.Ui/Theme/Icons.axaml";

    /// <inheritdoc cref="AvaloniaIconsPath"/>
    public const string FlutterStatusMarkPath = "src/Aer.Mobile/lib/theme/status_mark.dart";

    /// <summary>
    /// Every status's mark, as (status name, shape name, Avalonia geometry key). Exposed so the gate
    /// checks the same values the generator emitted rather than re-deriving them — a gate with its
    /// own idea of the mapping drifts from the generator and then passes while the UI is wrong.
    /// </summary>
    public static IEnumerable<(string Status, string Mark, string GeometryKey)> StatusMarks(string tokensJson)
    {
        using var document = JsonDocument.Parse(tokensJson, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
        });

        // Materialized before the document is disposed — JsonElement is a view over it.
        return Entries(document.RootElement.GetProperty("status"))
            .Select(entry => (entry.Name, MarkName(entry.Value), MarkGeometryKey(entry.Value)))
            .ToList();
    }

    /// <summary>The regeneration command, quoted in both the banner and the CI gate's failure text.</summary>
    public const string RegenerateCommand = "pixi run tokens";

    /// <summary>
    /// Where the shipped font files live inside <c>Aer.Ui</c>, as an Avalonia resource URI. The
    /// directory is covered by that project's <c>Assets\**</c> <c>AvaloniaResource</c> glob, so a
    /// file dropped there is embedded without further wiring.
    /// </summary>
    private const string AvaloniaFontAssetPath = "avares://Aer.Ui/Assets/Fonts";

    /// <summary>The same fonts' location in the Flutter project, as declared in <c>pubspec.yaml</c>.</summary>
    private const string FlutterFontAssetPath = "assets/fonts";

    private const string BannerLine1 = "GENERATED FILE — DO NOT EDIT.";

    private static readonly string[] BannerBody =
    [
        BannerLine1,
        $"Source: {TokensPath}",
        $"Regenerate: {RegenerateCommand}",
        "",
        "Hand edits are reverted by the next regeneration and fail CI in the meantime",
        "(Aer.Architecture.Tests). Change the token file instead.",
    ];

    /// <summary>
    /// The do-not-edit header in the target language's comment syntax. Dart has no block comment in
    /// idiomatic use, so <paramref name="commentClose"/> being null selects per-line comments —
    /// emitting a block form into Dart would produce a file that does not parse.
    /// </summary>
    private static string Banner(string commentOpen, string? commentClose)
        => BannerFor(BannerBody, commentOpen, commentClose);

    /// <summary>
    /// The banner for artifacts generated from <see cref="InteractionStatesPath"/> (#616 added a
    /// second source file, so "change the token file" would misdirect).
    /// </summary>
    private static string InteractionStatesBanner(string commentOpen, string? commentClose)
        => BannerFor(
            [
                BannerLine1,
                $"Source: {InteractionStatesPath}",
                $"Regenerate: {RegenerateCommand}",
                "",
                "Hand edits are reverted by the next regeneration and fail CI in the meantime",
                "(Aer.Architecture.Tests). Change the register file instead.",
            ],
            commentOpen,
            commentClose);

    private static string BannerFor(string[] bannerBody, string commentOpen, string? commentClose)
    {
        var banner = new StringBuilder();
        if (commentClose is null)
        {
            foreach (var line in bannerBody)
            {
                banner.AppendLine(line.Length == 0 ? commentOpen : $"{commentOpen} {line}");
            }

            return banner.ToString().TrimEnd();
        }

        banner.AppendLine(commentOpen);
        foreach (var line in bannerBody)
        {
            banner.AppendLine(line.Length == 0 ? string.Empty : $"    {line}");
        }

        banner.Append(commentClose);
        return banner.ToString();
    }

    // ---- colour helpers -------------------------------------------------------------------

    /// <summary>
    /// Every colour token carries a <c>light</c> and a <c>dark</c> value. Missing either is a
    /// malformed token file, not a default to invent — a silently substituted colour is precisely the
    /// stale-and-unchecked failure this pipeline exists to remove.
    /// </summary>
    private static (string Light, string Dark) Variants(JsonElement token, string path)
    {
        if (!token.TryGetProperty("light", out var light) || !token.TryGetProperty("dark", out var dark))
        {
            throw new InvalidOperationException(
                $"Colour token '{path}' must define both 'light' and 'dark'.");
        }

        return (light.GetString()!, dark.GetString()!);
    }

    private static IEnumerable<(string Name, JsonElement Value)> Entries(JsonElement parent) =>
        parent.EnumerateObject()
            .Where(property => !property.Name.StartsWith('$'))
            .Select(property => (property.Name, property.Value));

    /// <summary>
    /// A density block's numbers, flattening one level of nesting so <c>typeScale.title</c> emits as
    /// <c>TypeScaleTitle</c>. Flattened rather than skipped: the per-density type sizes are the
    /// difference between the two densities, so dropping them would silently generate a "density"
    /// that only changed padding.
    /// </summary>
    private static IEnumerable<(string Name, double Value)> DensityNumbers(JsonElement density)
    {
        foreach (var (name, value) in Entries(density))
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                yield return (Pascal(name), value.GetDouble());
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                foreach (var (nestedName, nestedValue) in Entries(value))
                {
                    yield return (Pascal(name) + Pascal(nestedName), nestedValue.GetDouble());
                }
            }
        }
    }

    private static string Number(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Pascal(string camel) => char.ToUpperInvariant(camel[0]) + camel[1..];

    /// <summary>The mark's shape name, as authored in the token file (#458) — <c>ring</c>, <c>check</c>, …</summary>
    private static string MarkName(JsonElement statusToken) => statusToken.GetProperty("mark").GetString()!;

    /// <summary>
    /// The Avalonia resource key of the <c>StreamGeometry</c> that draws a status's mark. The naming
    /// convention (<c>Icon.</c> + the Pascal-cased shape name) is what lets the drift gate check that
    /// <c>Icons.axaml</c> actually defines a shape for every status, rather than discovering a missing
    /// mark as a blank space in the running UI.
    /// </summary>
    private static string MarkGeometryKey(JsonElement statusToken) => "Icon." + Pascal(MarkName(statusToken));

    /// <summary>
    /// Whether the mark is painted solid rather than stroked (#461). Declared in the token file so
    /// both toolkits read one instruction: before this, Avalonia's call sites set only
    /// <c>Stroke</c> while the Flutter painter filled the same closed path, and the same status drew
    /// as an outline on desktop and a solid on mobile.
    /// </summary>
    private static bool MarkFilled(JsonElement statusToken) =>
        statusToken.TryGetProperty("filled", out var filled) && filled.GetBoolean();

    /// <summary>
    /// <c>#RRGGBB</c> as Dart's <c>0xFFRRGGBB</c>. Flutter has no hex-string colour literal, so the
    /// alpha channel has to be made explicit here rather than at every call site.
    /// </summary>
    private static string DartColor(string hex) => "0xFF" + hex.TrimStart('#').ToUpperInvariant();

    // ---- Avalonia -------------------------------------------------------------------------

    /// <summary>
    /// The app's semantic colour vocabulary (the <c>Color.*</c> keys views bind by name) mapped to
    /// the <c>tokens.json</c> path (<c>group.name</c>) it resolves to. Only keys whose target has a
    /// generated twin belong here; a legacy key without one (Color.Surface, Color.BorderSubtle, the
    /// accent hover/pressed/subtle family, the Status.*Bg tints, vendor marks) stays defined in
    /// Theme/Tokens.axaml until its value is added to design/tokens.json. Each becomes ONE brush per
    /// theme variant (see GenerateAvalonia) so a brush resolves to its own variant's colour.
    /// </summary>
    private static readonly (string Key, string Group, string Name)[] SemanticBrushAliases =
    {
        ("Color.Accent", "brand", "accent"),
        ("Color.OnAccent", "brand", "onAccent"),
        ("Color.Background", "surface", "ground"),
        ("Color.SurfaceRaised", "surface", "raised"),
        ("Color.SurfaceSubtle", "surface", "sunk"),
        ("Color.Border", "surface", "rule"),
        ("Color.Text", "text", "primary"),
        ("Color.TextSecondary", "text", "secondary"),
        // #1135: the status vocabulary the desktop views bind by name. Each brush alias
        // takes the SAME token value as its Status<X>Color twin above, so the two spellings cannot
        // drift — the hand-authored Tokens.axaml copies of these had already diverged (Stale was
        // purple where the token file's own prose says quiet states share one muted colour). The
        // Status.*Bg tints have no token yet and stay in Tokens.axaml.
        ("Status.Working", "status", "working"),
        ("Status.NeedsInput", "status", "needsInput"),
        ("Status.Finished", "status", "finished"),
        ("Status.Failed", "status", "failed"),
        ("Status.Idle", "status", "idle"),
        ("Status.Unavailable", "status", "unavailable"),
        ("Status.OutOfPlan", "status", "outOfPlan"),
    };

    /// <summary>
    /// A <c>ResourceDictionary</c> with <c>ThemeDictionaries</c> for Light and Dark, which is what
    /// lets Avalonia follow the OS preference: the app sets <c>ThemeVariant.Default</c> and the
    /// correct set resolves per variant, so "system" needs no code of its own.
    /// </summary>
    private static string GenerateAvalonia(JsonElement root)
    {
        var light = new StringBuilder();
        var dark = new StringBuilder();

        void EmitColorGroup(string groupName, string prefix)
        {
            foreach (var (name, token) in Entries(root.GetProperty(groupName)))
            {
                var key = prefix + Pascal(name);
                if (groupName == "status")
                {
                    var (statusLight, statusDark) = Variants(token, $"{groupName}.{name}");
                    light.AppendLine($"""      <Color x:Key="{key}Color">{statusLight}</Color>""");
                    dark.AppendLine($"""      <Color x:Key="{key}Color">{statusDark}</Color>""");
                    continue;
                }

                var (lightValue, darkValue) = Variants(token, $"{groupName}.{name}");
                light.AppendLine($"""      <Color x:Key="{key}Color">{lightValue}</Color>""");
                dark.AppendLine($"""      <Color x:Key="{key}Color">{darkValue}</Color>""");
            }
        }

        EmitColorGroup("brand", "Brand");
        EmitColorGroup("surface", "Surface");
        EmitColorGroup("text", "Text");
        EmitColorGroup("status", "Status");

        // The semantic colour brushes the Avalonia views bind by name (Color.Accent, Color.Text, …),
        // materialized from the same tokens as the <Color>s above so blue->teal is one source of
        // truth (design/tokens.json) and cannot be half-applied. One brush per variant, inside its
        // ThemeDictionary: a single shared brush cannot hold two colours at once, so the dark window
        // would render the light accent (#209's per-variant regression test catches exactly that).
        // Only keys with a generated twin appear; those without one still live in Tokens.axaml.
        foreach (var (key, group, name) in SemanticBrushAliases)
        {
            var (lightValue, darkValue) = Variants(root.GetProperty(group).GetProperty(name), $"{group}.{name}");
            light.AppendLine($"""      <SolidColorBrush x:Key="{key}">{lightValue}</SolidColorBrush>""");
            dark.AppendLine($"""      <SolidColorBrush x:Key="{key}">{darkValue}</SolidColorBrush>""");
        }

        var invariant = new StringBuilder();

        foreach (var (name, value) in Entries(root.GetProperty("radius")))
        {
            invariant.AppendLine($"""    <CornerRadius x:Key="Radius{Pascal(name)}">{Number(value.GetDouble())}</CornerRadius>""");
        }

        foreach (var (name, value) in Entries(root.GetProperty("spacing")))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="Spacing{Pascal(name)}">{Number(value.GetDouble())}</sys:Double>""");
        }

        foreach (var (name, role) in Entries(root.GetProperty("type").GetProperty("role")))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="FontSize{Pascal(name)}">{Number(role.GetProperty("size").GetDouble())}</sys:Double>""");
        }

        // Mark and label travel with the colour deliberately: 0006 requires every status to read
        // without hue, so a surface that can reach the colour must be able to reach both of these.
        // The mark is emitted as the *resource key* of a geometry rather than as a character —
        // see MarkGeometryKey and the token file's own note on why a codepoint cannot work.
        foreach (var (name, token) in Entries(root.GetProperty("status")))
        {
            invariant.AppendLine($"""    <sys:String x:Key="Status{Pascal(name)}Mark">{MarkGeometryKey(token)}</sys:String>""");
            invariant.AppendLine($"""    <sys:String x:Key="Status{Pascal(name)}Label">{token.GetProperty("label").GetString()}</sys:String>""");
        }

        var desktop = root.GetProperty("density").GetProperty("desktop");
        foreach (var (name, value) in DensityNumbers(desktop))
        {
            invariant.AppendLine($"""    <sys:Double x:Key="Density{name}">{Number(value)}</sys:Double>""");
        }

        // Resolved to the *shipped* asset, never a bare family name (#453, decision 0006): an
        // avares: URI can only resolve to the file in Assets/Fonts, so it cannot silently fall back
        // to whatever the machine happens to have installed.
        var fontFamily = root.GetProperty("type").GetProperty("fontFamily");
        foreach (var (role, family) in Entries(fontFamily))
        {
            invariant.AppendLine(
                $"""    <FontFamily x:Key="Font{Pascal(role)}">{AvaloniaFontAssetPath}#{family.GetString()}</FontFamily>""");
        }

        return $"""
        {Banner("<!--", "-->")}
        <ResourceDictionary xmlns="https://github.com/avaloniaui"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            xmlns:sys="clr-namespace:System;assembly=System.Runtime">
          <ResourceDictionary.ThemeDictionaries>
            <ResourceDictionary x:Key="Light">
        {light.ToString().TrimEnd()}
            </ResourceDictionary>
            <ResourceDictionary x:Key="Dark">
        {dark.ToString().TrimEnd()}
            </ResourceDictionary>
          </ResourceDictionary.ThemeDictionaries>

        {invariant.ToString().TrimEnd()}
        </ResourceDictionary>

        """.ReplaceLineEndings("\n");
    }

    // ---- Flutter --------------------------------------------------------------------------

    private static string GenerateFlutter(JsonElement root)
    {
        var colors = new StringBuilder();

        void EmitColorGroup(string groupName, string prefix)
        {
            foreach (var (name, token) in Entries(root.GetProperty(groupName)))
            {
                var (light, dark) = Variants(token, $"{groupName}.{name}");
                colors.AppendLine($"  static const Color {prefix}{Pascal(name)}Light = Color({DartColor(light)});");
                colors.AppendLine($"  static const Color {prefix}{Pascal(name)}Dark = Color({DartColor(dark)});");
            }
        }

        EmitColorGroup("brand", "brand");
        EmitColorGroup("surface", "surface");
        EmitColorGroup("text", "text");
        EmitColorGroup("status", "status");

        var scalars = new StringBuilder();
        foreach (var (name, value) in Entries(root.GetProperty("radius")))
        {
            scalars.AppendLine($"  static const double radius{Pascal(name)} = {Number(value.GetDouble())};");
        }

        foreach (var (name, value) in Entries(root.GetProperty("spacing")))
        {
            scalars.AppendLine($"  static const double spacing{Pascal(name)} = {Number(value.GetDouble())};");
        }

        foreach (var (name, role) in Entries(root.GetProperty("type").GetProperty("role")))
        {
            scalars.AppendLine($"  static const double fontSize{Pascal(name)} = {Number(role.GetProperty("size").GetDouble())};");
        }

        // Safe as bare family names here only because pubspec.yaml declares each one against a file
        // under FlutterFontAssetPath; a family Flutter cannot find falls back to Roboto silently,
        // which is the per-device resolution decision 0006 rules out.
        var fontFamily = root.GetProperty("type").GetProperty("fontFamily");
        foreach (var (role, family) in Entries(fontFamily))
        {
            scalars.AppendLine($"  static const String font{Pascal(role)} = '{family.GetString()}';");
        }

        var mobile = root.GetProperty("density").GetProperty("mobile");
        foreach (var (name, value) in DensityNumbers(mobile))
        {
            scalars.AppendLine($"  static const double density{name} = {Number(value)};");
        }

        var statusEnum = new StringBuilder();
        var statusGlyph = new StringBuilder();
        var statusLabel = new StringBuilder();
        var statusLight = new StringBuilder();
        var statusDark = new StringBuilder();
        var statusFilled = new StringBuilder();

        foreach (var (name, token) in Entries(root.GetProperty("status")))
        {
            statusEnum.AppendLine($"  {name},");
            statusFilled.AppendLine($"        AerStatus.{name} => {(MarkFilled(token) ? "true" : "false")},");
            statusGlyph.AppendLine($"        AerStatus.{name} => '{MarkName(token)}',");
            statusLabel.AppendLine($"        AerStatus.{name} => '{token.GetProperty("label").GetString()}',");
            statusLight.AppendLine($"        AerStatus.{name} => AerTokens.status{Pascal(name)}Light,");
            statusDark.AppendLine($"        AerStatus.{name} => AerTokens.status{Pascal(name)}Dark,");
        }

        var motion = root.GetProperty("motion");

        return $$"""
        {{Banner("//", null)}}
        import 'package:flutter/material.dart';

        /// Raw token values. Prefer [aerTheme] over reaching for these directly.
        class AerTokens {
        {{colors.ToString().TrimEnd()}}

        {{scalars.ToString().TrimEnd()}}

          static const Duration durationQuick = Duration(milliseconds: {{Number(motion.GetProperty("durationQuickMs").GetDouble())}});
          static const Duration durationStandard = Duration(milliseconds: {{Number(motion.GetProperty("durationStandardMs").GetDouble())}});
        }

        /// The five states from #334's split.
        enum AerStatus {
        {{statusEnum.ToString().TrimEnd()}}
        }

        /// Decision 0006: a status must never be conveyed by hue alone, so every state carries a
        /// mark and a word. Render [mark] and [label] together - colour is the third channel, not
        /// the only one.
        ///
        /// [mark] names a shape, not a character (#458): the shipped faces do not cover the
        /// codepoints this originally used, and between them have no checkmark and no cross at all.
        /// `StatusMark` in status_mark.dart draws it; desktop draws the same shape from a
        /// StreamGeometry of the matching name.
        extension AerStatusPresentation on AerStatus {
          String get mark => switch (this) {
        {{statusGlyph.ToString().TrimEnd()}}
              };

          /// Whether [mark]'s shape is painted solid rather than stroked. Stated in the token file so
          /// both toolkits obey one instruction - Avalonia's call sites once set only Stroke while this
          /// painter filled the same closed path, drawing one status two different ways (#461).
          bool get markFilled => switch (this) {
        {{statusFilled.ToString().TrimEnd()}}
              };

          String get label => switch (this) {
        {{statusLabel.ToString().TrimEnd()}}
              };

          Color color(Brightness brightness) => brightness == Brightness.dark
              ? switch (this) {
        {{statusDark.ToString().TrimEnd()}}
                }
              : switch (this) {
        {{statusLight.ToString().TrimEnd()}}
                };
        }

        /// Builds [ThemeData] for one brightness. Pass both to `MaterialApp(theme:, darkTheme:)` with
        /// `themeMode: ThemeMode.system` - that is the whole of "system" support; Flutter resolves the
        /// OS preference itself once both are supplied.
        ThemeData aerTheme(Brightness brightness) {
          final isDark = brightness == Brightness.dark;
          final accent = isDark ? AerTokens.brandAccentDark : AerTokens.brandAccentLight;
          final onAccent = isDark ? AerTokens.brandOnAccentDark : AerTokens.brandOnAccentLight;
          final ground = isDark ? AerTokens.surfaceGroundDark : AerTokens.surfaceGroundLight;
          final raised = isDark ? AerTokens.surfaceRaisedDark : AerTokens.surfaceRaisedLight;
          final rule = isDark ? AerTokens.surfaceRuleDark : AerTokens.surfaceRuleLight;
          final primary = isDark ? AerTokens.textPrimaryDark : AerTokens.textPrimaryLight;
          final secondary = isDark ? AerTokens.textSecondaryDark : AerTokens.textSecondaryLight;

          return ThemeData(
            brightness: brightness,
            fontFamily: AerTokens.fontSans,
            scaffoldBackgroundColor: ground,
            colorScheme: ColorScheme.fromSeed(
              seedColor: accent,
              brightness: brightness,
            ).copyWith(
              primary: accent,
              onPrimary: onAccent,
              surface: raised,
              onSurface: primary,
              outline: rule,
            ),
            dividerColor: rule,
            textTheme: TextTheme(
              titleMedium: TextStyle(
                fontSize: AerTokens.densityTypeScaleTitle,
                fontWeight: FontWeight.w600,
                color: primary,
              ),
              bodyMedium: TextStyle(fontSize: AerTokens.fontSizeBody, color: primary),
              bodySmall: TextStyle(
                fontSize: AerTokens.densityTypeScaleSecondary,
                color: secondary,
              ),
            ),
          );
        }

        """.ReplaceLineEndings(Lf);
    }

    // ---- Aer.Ui.Core ----------------------------------------------------------------------

    /// <summary>
    /// The five states, their marks and their labels as ordinary C#, so the desktop side reaches
    /// them the same way Flutter already does. Deliberately emitted into the Avalonia-free core
    /// project: nothing here is a toolkit type, and putting it in <c>Aer.Ui</c> would make the
    /// remote/mobile-facing ViewModels unable to name a status.
    /// </summary>
    private static string GenerateUiCore(JsonElement root)
    {
        var members = new StringBuilder();
        var marks = new StringBuilder();
        var labels = new StringBuilder();
        var colors = new StringBuilder();
        var fills = new StringBuilder();

        foreach (var (name, token) in Entries(root.GetProperty("status")))
        {
            members.AppendLine($"    {Pascal(name)},");
            marks.AppendLine($"""        AerStatus.{Pascal(name)} => "{MarkGeometryKey(token)}",""");
            labels.AppendLine($"""        AerStatus.{Pascal(name)} => "{token.GetProperty("label").GetString()}",""");
            colors.AppendLine($"""        AerStatus.{Pascal(name)} => "Status{Pascal(name)}Color",""");
            fills.AppendLine($"        AerStatus.{Pascal(name)} => {(MarkFilled(token) ? "true" : "false")},");
        }

        return $$"""
        {{Banner("//", null)}}

        namespace Aer.Ui.Core;

        /// <summary>The five states from #334's split — the vocabulary every status-rendering surface uses.</summary>
        public enum AerStatus
        {
        {{members.ToString().TrimEnd()}}
        }

        /// <summary>
        /// Decision 0006: a status must never be conveyed by hue alone, so every state carries a mark
        /// and a word. Any surface that renders <see cref="ColorResourceKey"/> must also render
        /// <see cref="MarkResourceKey"/> and <see cref="Label"/> — colour is the third channel, never
        /// the only one.
        /// </summary>
        public static class AerStatusPresentation
        {
            /// <summary>
            /// The resource key of the <c>StreamGeometry</c> that draws this status's mark, defined in
            /// <c>Aer.Ui/Theme/Icons.axaml</c>. A shape rather than a character: the shipped faces do not
            /// cover the codepoints originally chosen, and between them carry no checkmark and no cross
            /// at all, so a text glyph cannot express this set on both platforms (#458).
            /// </summary>
            public static string MarkResourceKey(this AerStatus status) => status switch
            {
        {{marks.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
            };

            /// <summary>The status in words — rendered alongside the mark, never replaced by it.</summary>
            public static string Label(this AerStatus status) => status switch
            {
        {{labels.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
            };

            /// <summary>
            /// The key of this status's <c>Color</c> in the generated theme dictionaries. A colour, not a
            /// brush: it resolves per theme variant, so a consumer must look it up against the live
            /// variant rather than through the theme-oblivious overload (the washed-out DAG boxes of
            /// #204/#205 were exactly that mistake).
            /// </summary>
            public static string ColorResourceKey(this AerStatus status) => status switch
            {
        {{colors.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
            };

            /// <summary>
            /// Whether this status's mark is painted solid rather than stroked. Stated in the token file
            /// so both toolkits obey one instruction: the Avalonia call sites previously set only
            /// <c>Stroke</c> while Flutter's painter filled the same closed path, so one status drew as an
            /// outline on desktop and a solid on mobile (#461).
            /// </summary>
            public static bool MarkIsFilled(this AerStatus status) => status switch
            {
        {{fills.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped status."),
            };
        }

        """.ReplaceLineEndings(Lf);
    }

    private static string GenerateInteractionStates(JsonElement root)
    {
        var members = new StringBuilder();
        var names = new StringBuilder();
        var behaviours = new StringBuilder();

        foreach (var (key, state) in Entries(root.GetProperty("states")))
        {
            var member = Pascal(key);
            members.AppendLine($"    {member},");
            names.AppendLine($"""        InteractionState.{member} => "{state.GetProperty("name").GetString()}",""");
            behaviours.AppendLine($"""        InteractionState.{member} => "{Escape(state.GetProperty("behaviour").GetString()!)}",""");
        }

        return $$"""
        {{InteractionStatesBanner("//", null)}}

        namespace Aer.Ui.Core;

        /// <summary>
        /// The interaction states — the situations every surface must handle (#616; ratified
        /// thirteen on #495). A different population from <see cref="AerStatus"/>: that is the
        /// room-lifecycle vocabulary, this is the screen-situation inventory; they overlap at
        /// record-once-ok: #443 design/interaction-states.json
        /// Cancelled/Failed only. 0020's rules govern consumption: rendering is a projection,
        /// absence is not a state — which is why the presentation methods below throw on an
        /// unmapped member instead of answering with a default.
        /// </summary>
        public enum InteractionState
        {
        {{members.ToString().TrimEnd()}}
        }

        public static class InteractionStatePresentation
        {
            /// <summary>The state's display name, as the register records it.</summary>
            public static string DisplayName(this InteractionState state) => state switch
            {
        {{names.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped interaction state."),
            };

            /// <summary>What a surface holding this state does — the register's behaviour sentence.</summary>
            public static string Behaviour(this InteractionState state) => state switch
            {
        {{behaviours.ToString().TrimEnd()}}
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped interaction state."),
            };
        }

        """.ReplaceLineEndings(Lf);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Generated artifacts are always LF, on every platform. Otherwise the CI gate would compare a
    /// CRLF regeneration on Windows against an LF file checked in from Linux and fail on line
    /// endings alone — a gate that fires on nothing real is a gate that gets turned off.
    /// </summary>
    private const string Lf = "\n";
}
