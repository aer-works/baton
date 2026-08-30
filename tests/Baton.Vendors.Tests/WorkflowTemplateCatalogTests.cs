using System.Text.Json;
using Baton.Vendors;
using Xunit;

namespace Baton.Vendors.Tests;

/// <summary>
/// Proves that workflow templates resolve from data over the worker-role catalog, that env overrides
/// take precedence, and that load-time validation catches empty catalogs, duplicate ids, zero-phase
/// templates, unknown role references, duplicate phase names, and invalid input declarations.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public class WorkflowTemplateCatalogTests
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
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"wtc-{Guid.NewGuid():N}");

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

    private static string Phase(
        string name = "implement",
        string roleId = "implement",
        string instruction = "instr",
        bool askFirst = false,
        string inputs = "[]") =>
        $$"""{"name":"{{name}}","role_id":"{{roleId}}","instruction":"{{instruction}}","ask_first":{{(askFirst ? "true" : "false")}},"inputs":{{inputs}}}""";

    private static string Template(string id, string phases) =>
        $$"""{"id":"{{id}}","phases":{{phases}}}""";

    private static EnvScope PointAt(TempCatalog cat, string templatesJson) =>
        new EnvScope()
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"))
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"))
            .Set(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, cat.Write("templates.json", templatesJson));

    private static EnvScope ShippedDefault() =>
        new EnvScope()
            .Set(WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"))
            .Set(WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"))
            .Set(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"));

    [Fact]
    public void The_shipped_catalog_resolves_and_roundtrips()
    {
        using var env = ShippedDefault();

        var template = WorkflowTemplateCatalog.For("implement-review");
        Assert.Equal("implement-review", template.Id);
        Assert.Equal(3, template.Phases.Count);

        Assert.Equal("implement", template.Phases[0].Name);
        Assert.Equal("implement", template.Phases[0].RoleId);
        Assert.Equal("Implement the change described in the spec.", template.Phases[0].Instruction);
        Assert.False(template.Phases[0].AskFirst);
        Assert.Empty(template.Phases[0].Inputs);

        Assert.Equal("janitor", template.Phases[1].Name);
        Assert.Equal("janitor", template.Phases[1].RoleId);
        Assert.Equal("Commit the work and prepare the tree.", template.Phases[1].Instruction);
        Assert.False(template.Phases[1].AskFirst);
        Assert.Empty(template.Phases[1].Inputs);

        Assert.Equal("review", template.Phases[2].Name);
        Assert.Equal("review", template.Phases[2].RoleId);
        Assert.Equal("Review the implemented change against the spec.", template.Phases[2].Instruction);
        Assert.False(template.Phases[2].AskFirst);

        var input = Assert.Single(template.Phases[2].Inputs);
        Assert.Equal("diff-of-work-so-far", input);
    }

    [Fact]
    public void The_env_override_takes_precedence()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("custom-tpl", $"[{Phase(name: "custom-phase", roleId: "implement", instruction: "custom instr")}]")}]");

        var template = WorkflowTemplateCatalog.For("custom-tpl");
        Assert.Equal("custom-tpl", template.Id);
        Assert.Equal("custom-phase", template.Phases[0].Name);
        Assert.Equal("implement", template.Phases[0].RoleId);
        Assert.Equal("custom instr", template.Phases[0].Instruction);
    }

    [Fact]
    public void An_empty_catalog_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(cat, "[]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_duplicate_template_id_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("dup", $"[{Phase()}]")},{Template("dup", $"[{Phase()}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_phase_template_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(cat, $"[{Template("zero", "[]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("zero", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phase_naming_an_unknown_role_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("t", $"[{Phase(roleId: "unknown-role-id")}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("unknown-role-id", ex.Message, StringComparison.Ordinal);
        Assert.Contains("implement", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phase_declaring_an_unknown_input_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("t", $"[{Phase(inputs: """["unknown-input"]""")}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("unknown-input", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_unknown_id_throws_KeyNotFoundException()
    {
        using var env = ShippedDefault();

        var ex = Assert.Throws<KeyNotFoundException>(() => WorkflowTemplateCatalog.For("does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("implement-review", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_missing_id_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"phases":[{"name":"p1","role_id":"implement","instruction":"i","ask_first":false,"inputs":[]}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_template_missing_phases_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t"}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_missing_name_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"role_id":"implement","instruction":"i","ask_first":false,"inputs":[]}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_missing_role_id_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"name":"p1","instruction":"i","ask_first":false,"inputs":[]}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_missing_instruction_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"name":"p1","role_id":"implement","ask_first":false,"inputs":[]}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_missing_ask_first_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"name":"p1","role_id":"implement","instruction":"i","inputs":[]}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_missing_inputs_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"name":"p1","role_id":"implement","instruction":"i","ask_first":false}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_with_null_inputs_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("t", $"[{Phase(inputs: "null")}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("null inputs", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_phases_with_the_same_name_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("t", $"[{Phase(name: "dup-phase")},{Phase(name: "dup-phase", roleId: "janitor")}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("dup-phase", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_json_field_on_a_phase_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            """[{"id":"t","phases":[{"name":"p1","role_id":"implement","instruction":"i","ask_first":false,"inputs":[],"timeout_minutes":10}]}]""");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }

    [Fact]
    public void A_phase_with_blank_name_throws()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            $"[{Template("t", $"[{Phase(name: "  ")}]")}]");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = WorkflowTemplateCatalog.All);
        Assert.Contains("blank name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_catalog_file_with_comments_fails_loudly()
    {
        using var cat = new TempCatalog();
        using var env = PointAt(
            cat,
            "[\n  // inline comment\n  " + Template("t", $"[{Phase()}]") + "\n]");

        Assert.Throws<JsonException>(() => _ = WorkflowTemplateCatalog.All);
    }
}
