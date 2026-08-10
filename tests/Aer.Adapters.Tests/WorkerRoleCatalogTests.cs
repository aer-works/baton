using System.Linq;
using System.Text.Json;
using Aer.Adapters;
using Aer.Flow.Domain;
using Xunit;

namespace Aer.Adapters.Tests;

/// <summary>
/// #888: the shared worker-role catalog. Proves a role resolves its vendor/model/effort from its
/// tier (so a role never hardcodes a model), that a tier edit reaches every role on it with no
/// rebuild (the env override stands in for the runtime <c>worker-tiers.json</c> the operator drops),
/// and that a malformed catalog fails loudly rather than dispatching something nobody chose.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public class WorkerRoleCatalogTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly List<(string Key, string? Prior)> _prior = [];

        public EnvScope Set(string key, string? value)
        {
            _prior.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (key, prior) in _prior)
            {
                Environment.SetEnvironmentVariable(key, prior);
            }
        }
    }

    private sealed class TempCatalog : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"wrc-{Guid.NewGuid():N}");

        public TempCatalog() => Directory.CreateDirectory(Dir);

        public string Write(string name, string content)
        {
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            DirectoryCleanup.DeleteRecursively(Dir);
        }
    }

    private const string DefaultOutputs = """[{"name":"out.md","schema":"none","instruction":"Write to out.md."}]""";

    private static string Role(string id, string tier, bool write = false, bool shell = false, bool net = false,
        int timeout = 10, bool verdict = false, string outputs = DefaultOutputs) =>
        $$"""
          {"id":"{{id}}","tier":"{{tier}}","read_files":true,"write_files":{{(write ? "true" : "false")}},
           "run_shell_commands":{{(shell ? "true" : "false")}},"network_access":{{(net ? "true" : "false")}},
           "timeout_minutes":{{timeout}},"verdict_schema":{{(verdict ? "true" : "false")}},"purpose":"p","outputs":{{outputs}}}
          """;

    private static EnvScope PointAt(TempCatalog cat, string tiersJson, string rolesJson) =>
        new EnvScope()
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, cat.Write("tiers.json", tiersJson))
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, cat.Write("roles.json", rolesJson));

    // A test that reads the SHIPPED default must be hermetic against the runtime overrides: with no
    // env set, ResolvePath falls through {AER_HOME|~/.aer}/worker-*.json, so on a machine where an
    // operator has used that documented override the test would silently read their file instead of
    // the shipped one. Point the catalog's OWN env vars straight at the shipped files under
    // AppContext.BaseDirectory (copied there by the csproj's CopyToOutputDirectory). Deliberately NOT
    // via AER_HOME: that variable is global process state AerPaths.Root reads, so mutating it here
    // raced a parallel AerProfileStore.DefaultPath and red an unrelated test (#893). AER_WORKER_*_PATH
    // is read only by WorkerRoleCatalog, so nothing else can see it.
    private static EnvScope ShippedDefault() =>
        new EnvScope()
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"))
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"));

    [Fact]
    public void The_shipped_catalog_resolves_each_role_against_its_tier()
    {
        using var env = ShippedDefault();

        var review = WorkerRoleCatalog.For("review");
        Assert.Equal("claude", review.Adapter);
        Assert.Equal("sonnet", review.Model);
        Assert.Equal("high", review.Effort);
        Assert.False(review.Grant.WriteFiles);
        Assert.True(review.ProducesVerdict);

        var implement = WorkerRoleCatalog.For("implement");
        Assert.Equal("agy", implement.Adapter);
        Assert.True(implement.Grant.RunShellCommands);
        Assert.True(implement.Grant.NetworkAccess);
        Assert.False(implement.ProducesVerdict);
        Assert.Equal(TimeSpan.FromMinutes(40), implement.Timeout);
    }

    [Fact]
    public void One_tier_edit_reaches_every_role_on_that_tier_with_no_rebuild()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"shared":{"adapter":"gemini","model":"a-future-model","effort":null}}""",
            $"[{Role("a", "shared")},{Role("b", "shared", write: true)}]");

        Assert.Equal("a-future-model", WorkerRoleCatalog.For("a").Model);
        Assert.Equal("a-future-model", WorkerRoleCatalog.For("b").Model);
        Assert.False(WorkerRoleCatalog.For("a").Grant.WriteFiles);
        Assert.True(WorkerRoleCatalog.For("b").Grant.WriteFiles);
    }

    [Fact]
    public void A_role_naming_an_undefined_tier_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"known":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("x", "missing")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void A_duplicate_role_id_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("dup", "t")},{Role("dup", "t")}]");

        Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void An_unknown_role_id_throws_naming_the_known_ones()
    {
        using var env = ShippedDefault();

        var ex = Assert.Throws<KeyNotFoundException>(() => WorkerRoleCatalog.For("does-not-exist"));
        Assert.Contains("review", ex.Message);
    }

    [Fact]
    public void A_role_missing_a_required_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // `purpose` omitted. Without [JsonRequired] this would deserialize to a null Purpose and ship a
        // role nobody authored; the catalog's contract is to fail at load, not at dispatch.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            """[{"id":"x","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,"network_access":false,"timeout_minutes":10,"verdict_schema":false}]""");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void A_catalog_file_with_comments_fails_loudly_so_both_readers_agree()
    {
        using var cat = new TempCatalog();
        // dispatch.py reads the same files through stdlib json.loads, which rejects comments. The C#
        // reader must reject them too, or an operator's inline // WHY loads in the engine and breaks
        // every dispatch.
        using var env = PointAt(
            cat,
            "{\n  // #742 operator directive\n  \"t\":{\"adapter\":\"gemini\",\"model\":\"m\",\"effort\":null}\n}",
            $"[{Role("x", "t")}]");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void The_shipped_review_role_declares_a_prose_report_and_a_schema_checked_verdict()
    {
        using var env = ShippedDefault();

        var outputs = WorkerRoleCatalog.For("review").Outputs;

        var verdict = outputs.Single(o => o.Name == "verdict.json");
        Assert.Equal(OutputSchema.ReviewVerdict, verdict.Schema);
        Assert.Contains("verdict.json", verdict.Instruction, StringComparison.Ordinal);

        var prose = outputs.Single(o => o.Name == "report.md");
        Assert.Equal(OutputSchema.None, prose.Schema);
    }

    [Fact]
    public void The_shipped_mutation_roles_declare_their_handoff_outputs()
    {
        using var env = ShippedDefault();

        // implement's summary is a floor + handoff, existence-only -- its correctness is a review's job.
        var changes = Assert.Single(WorkerRoleCatalog.For("implement").Outputs);
        Assert.Equal("changes.md", changes.Name);
        Assert.Equal(OutputSchema.None, changes.Schema);

        // janitor declares its report AND branch.diff -- the diff is the ground truth a following review
        // reads (#789). Both named, so dropping either from the catalog fails here (the #741 failure was
        // a wrong filename on this exact role).
        var janitor = WorkerRoleCatalog.For("janitor").Outputs;
        Assert.Contains(janitor, o => o.Name == "janitor.md");
        Assert.Contains(janitor, o => o.Name == "branch.diff");
        Assert.All(janitor, o => Assert.Equal(OutputSchema.None, o.Schema));
    }

    [Fact]
    public void An_output_maps_its_schema_from_the_catalog_string()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"verdict.json","schema":"review_verdict","instruction":"i"}]""")}]");

        var output = Assert.Single(WorkerRoleCatalog.For("r").Outputs);
        Assert.Equal(OutputSchema.ReviewVerdict, output.Schema);
    }

    [Fact]
    public void An_output_with_an_unknown_schema_fails_loudly()
    {
        using var cat = new TempCatalog();
        // A typo'd schema must throw at load, not default to None and silently drop the verdict check.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"x","schema":"verdikt","instruction":"i"}]""")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("verdikt", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_missing_the_outputs_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // outputs is [JsonRequired] like every other field: an omitted array would deserialize to null
        // and ship a role that declares nothing, dispatching a worker told to write no artifact.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            """[{"id":"r","tier":"t","read_files":true,"write_files":false,"run_shell_commands":false,"network_access":false,"timeout_minutes":10,"verdict_schema":false,"purpose":"p"}]""");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void An_output_missing_a_required_field_fails_loudly()
    {
        using var cat = new TempCatalog();
        // instruction omitted -- without [JsonRequired] it would bind to null and dispatch a worker
        // never told to produce the file the contract then fails it for not producing.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":"x","schema":"none"}]""")}]");

        Assert.Throws<JsonException>(() => _ = WorkerRoleCatalog.All);
    }

    [Fact]
    public void A_role_declaring_an_empty_outputs_list_fails_loudly()
    {
        using var cat = new TempCatalog();
        // Present but empty: [JsonRequired] is satisfied, so only an explicit count guard catches this.
        // A role that declares nothing has no floor -- a silent no-op worker would pass.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: "[]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_declaring_a_null_outputs_value_fails_loudly_by_name()
    {
        using var cat = new TempCatalog();
        // outputs present but null passes [JsonRequired]; without the guard it throws an unnamed
        // ArgumentNullException out of Select, unlike every other failure here which names the role.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: "null")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains("r", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_named_with_a_leading_dot_fails_loudly_at_load()
    {
        using var cat = new TempCatalog();
        // '.'-prefixed names are reserved for engine stream logs; ProducedOutput refuses them at
        // dispatch, so the catalog must refuse them at load rather than defer the failure.
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("r", "t", outputs: """[{"name":".notes.md","schema":"none","instruction":"i"}]""")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkerRoleCatalog.All);
        Assert.Contains(".notes.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_patch_role_declares_a_patch_diff_output_with_diff_schema()
    {
        using var env = ShippedDefault();

        var patchRole = WorkerRoleCatalog.For("patch");
        Assert.False(patchRole.Grant.WriteFiles);
        Assert.False(patchRole.ProducesVerdict);

        var output = Assert.Single(patchRole.Outputs);
        Assert.Equal("patch.diff", output.Name);
        Assert.Equal(OutputSchema.Diff, output.Schema);
        Assert.Contains("patch.diff", output.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void An_output_maps_diff_schema_from_the_catalog_string()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """{"t":{"adapter":"gemini","model":"m","effort":null}}""",
            $"[{Role("p", "t", outputs: """[{"name":"patch.diff","schema":"diff","instruction":"i"}]""")}]");

        var output = Assert.Single(WorkerRoleCatalog.For("p").Outputs);
        Assert.Equal(OutputSchema.Diff, output.Schema);
    }

    [Fact]
    public void The_review_verdict_instruction_embeds_a_schema_valid_example_and_names_the_enum_sets()
    {
        // #1092: the instruction named "ReviewVerdict JSON" but showed no shape, so a strong model
        // guessed findings[].claim and the closed severity/status enums wrong and was rejected on
        // repeat (the schema traps are pinned in ReviewVerdictSchemaTests). It must now carry a
        // concrete example the schema accepts -- a wrong example would be worse than none -- and name
        // the status values a single example cannot show.
        var instruction = WorkerRoleCatalog.For("review").Outputs.Single(o => o.Name == "verdict.json").Instruction;

        var open = instruction.IndexOf('{');
        var close = instruction.LastIndexOf('}');
        Assert.True(open >= 0 && close > open, "the instruction embeds no JSON example object");
        var example = instruction.Substring(open, close - open + 1);
        Assert.True(
            ReviewVerdictSchema.TryParse(System.Text.Encoding.UTF8.GetBytes(example), out _, out var error),
            $"the instruction's example must parse as a ReviewVerdict: {error}");

        // status is the subtler closed set (confirmed/refuted/unverified); the example shows only one,
        // so the other two must be named or a model still guesses them.
        Assert.Contains("refuted", instruction);
        Assert.Contains("unverified", instruction);
    }
}
