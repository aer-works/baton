using System.Text.Json;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1623 (contract: <c>spec/baton.md</c> §3): "worker briefs stop asking for full
/// gates" — the engine runs the verify step now, so no worker role/template prompt should tell the
/// model to run the gate suite itself. Measured at the time this test was written: a repo-wide search
/// found no such instruction anywhere (<c>WorkerRoles.json</c>'s <c>implement</c> role names only
/// <c>changes.md</c>; <c>WorkflowTemplates.json</c>'s phase instructions name no gate/test/build
/// command) — this is a forward-regression guard, not proof of a removal, and the PR that added it
/// says so plainly rather than manufacturing something to point at.
/// </summary>
/// <remarks>
/// <para>
/// Checks only the strings a worker's own prompt is actually built from — a role's <c>purpose</c> and
/// each <c>outputs[].instruction</c>, and a template phase's <c>instruction</c> — not the whole JSON
/// file's text. #1623 itself added a <c>verify_pixi_task: "gates-quiet"</c> config value to
/// <c>WorkerRoles.json</c>'s <c>implement</c> role (the engine's own instruction to itself, read by
/// <c>MutationInterface</c>, never by the worker), so a raw whole-file substring scan would flag its
/// own change; parsing to the specific prompt-facing fields is what avoids that false positive while
/// still catching the real thing this guards against.
/// </para>
/// <para>
/// The one place a gate command name legitimately appears in a worker-facing string is
/// <c>AgyWorkerAdapter.ForegroundGateInstructionText</c> (#1625) — an instruction to run any slow
/// command (gates included) in the FOREGROUND rather than backgrounding it and polling, not an
/// instruction to run the gate suite at all. Not scanned by this test at all, by file, rather than by
/// wording — the wording itself is exactly what a future edit might legitimately change.
/// </para>
/// Pure file reading over the repo, no project references, matching <see cref="VendorSpawnGateTests"/>.
/// </remarks>
public sealed class NoFullGatesInstructionInWorkerTemplatesTests
{
    /// <summary>
    /// Case-insensitive substrings that would indicate a worker is being told to run the gate suite
    /// itself. Deliberately narrower than a bare "gate" match, which would also flag unrelated prose
    /// (e.g. a "grant gate" or "PreToolUse gate" reference) that has nothing to do with this ruling.
    /// </summary>
    private static readonly string[] FullGatesPhrases =
    [
        "run the gates", "run gates", "run the full gates", "pixi run gates", "gates-quiet", "gates-fast",
    ];

    [Fact]
    public void No_worker_role_prompt_field_instructs_the_model_to_run_the_gate_suite_itself()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "src", "Baton.Vendors", "WorkerRoles.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var violations = new List<string>();
        foreach (var role in doc.RootElement.EnumerateArray())
        {
            var roleId = role.TryGetProperty("id", out var idProp) ? idProp.GetString() : "<unknown>";

            CheckField(role, "purpose", $"WorkerRoles.json role '{roleId}'.purpose", violations);

            if (role.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var output in outputs.EnumerateArray())
                {
                    var outputName = output.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "<unknown>";
                    CheckField(output, "instruction", $"WorkerRoles.json role '{roleId}' output '{outputName}'.instruction", violations);
                }
            }
        }

        AssertNoViolations(violations);
    }

    [Fact]
    public void No_workflow_template_phase_instruction_tells_the_model_to_run_the_gate_suite_itself()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "src", "Baton.Vendors", "WorkflowTemplates.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var violations = new List<string>();
        foreach (var template in doc.RootElement.EnumerateArray())
        {
            var templateId = template.TryGetProperty("id", out var idProp) ? idProp.GetString() : "<unknown>";
            if (!template.TryGetProperty("phases", out var phases) || phases.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var phase in phases.EnumerateArray())
            {
                var phaseName = phase.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "<unknown>";
                CheckField(phase, "instruction", $"WorkflowTemplates.json template '{templateId}' phase '{phaseName}'.instruction", violations);
            }
        }

        AssertNoViolations(violations);
    }

    private static void CheckField(JsonElement parent, string fieldName, string label, List<string> violations)
    {
        if (!parent.TryGetProperty(fieldName, out var field) || field.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = field.GetString() ?? string.Empty;
        foreach (var phrase in FullGatesPhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{label}: contains '{phrase}'");
            }
        }
    }

    private static void AssertNoViolations(List<string> violations)
    {
        Assert.True(
            violations.Count == 0,
            "A worker-facing prompt field names the gate suite, which #1623 moved to the engine's own "
            + "verify step:\n  " + string.Join("\n  ", violations)
            + "\n\nIf this is meant as AgyWorkerAdapter.ForegroundGateInstructionText's own "
            + "foreground-vs-background wording, it belongs in that file only — do not let a gate "
            + "instruction leak into a role/template prompt field.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "pixi.toml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
