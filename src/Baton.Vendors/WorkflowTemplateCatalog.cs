using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Flow.Status;

namespace Baton.Vendors;

/// <summary>
/// A phase within a <see cref="WorkflowTemplate"/> — names a phase identity and worker role to run, along with
/// phase-specific instruction prose, an optional approval gate toggle, and symbolic inputs.
/// </summary>
/// <param name="Name">The unique identifier of the phase within its template.</param>
/// <param name="RoleId">The worker role to run for this phase (must resolve against <see cref="WorkerRoleCatalog"/>).</param>
/// <param name="Instruction">The prose body / instruction for this phase.</param>
/// <param name="AskFirst">Per-step gate toggle (decision 0025) — whether to prompt the operator before executing.</param>
/// <param name="Inputs">The list of inputs required by this phase (must be from the closed set).</param>
public sealed record WorkflowTemplatePhase(string Name, string RoleId, string Instruction, bool AskFirst, IReadOnlyList<string> Inputs);

/// <summary>
/// A reusable workflow template definition composed as data over the existing worker-role catalog.
/// </summary>
/// <param name="Id">The unique identifier of the workflow template.</param>
/// <param name="Phases">The ordered list of phases that make up the workflow template.</param>
public sealed record WorkflowTemplate(string Id, IReadOnlyList<WorkflowTemplatePhase> Phases);

/// <summary>
/// The runtime-resolved catalog of workflow templates.
/// </summary>
/// <remarks>
/// Resolution order per file, evaluated fresh on every access (<see cref="WorkerRoleCatalog"/> keeps
/// the same "resolve, never capture" discipline, for the same reason):
/// <list type="number">
/// <item>the <c>BATON_WORKFLOW_TEMPLATES_PATH</c> environment override, when set — for a one-off experiment;</item>
/// <item><c>{BatonPaths.Root}/workflow-templates.json</c> when it exists — the operator's durable, rebuild-free override;</item>
/// <item>the default shipped next to the assembly (<see cref="AppContext.BaseDirectory"/>).</item>
/// </list>
/// </remarks>
public static class WorkflowTemplateCatalog
{
    public const string TemplatesPathEnvironmentVariable = "BATON_WORKFLOW_TEMPLATES_PATH";

    private const string TemplatesDefaultFileName = "WorkflowTemplates.json";
    private const string TemplatesOverrideFileName = "workflow-templates.json";

    // Plain JSON only — no comments, no trailing commas.
    // Unmapped member handling is set to Disallow because templates are user-authored (unlike engine-shipped WorkerRoles.json), so typo'd or smuggled fields must fail loudly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // The engine-defined, closed set of valid inputs (decision 0047). New members are added here by the engine,
    // never supplied by a template author.
    private static readonly HashSet<string> ClosedInputs = new(StringComparer.Ordinal)
    {
        "diff-of-work-so-far",
    };

    /// <summary>Every workflow template in the catalog, in file order.</summary>
    public static IReadOnlyList<WorkflowTemplate> All => Load();

    /// <summary>The workflow template with <paramref name="id"/>, or throws if the catalog has no such template.</summary>
    public static WorkflowTemplate For(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No workflow template '{id}' in the catalog. Known templates: {string.Join(", ", All.Select(t => t.Id))}.");
    }

    private static IReadOnlyList<WorkflowTemplate> Load()
    {
        var rawTemplates = ReadJson<List<RawTemplate>>(
            ResolvePath(TemplatesPathEnvironmentVariable, TemplatesOverrideFileName, TemplatesDefaultFileName), "template list");

        if (rawTemplates.Count == 0)
        {
            throw new InvalidOperationException("The workflow-template catalog is empty.");
        }

        var knownRoleIds = WorkerRoleCatalog.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var templates = new List<WorkflowTemplate>(rawTemplates.Count);

        foreach (var raw in rawTemplates)
        {
            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"Duplicate workflow template id '{raw.Id}' in the catalog.");
            }

            if (raw.Phases is null || raw.Phases.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow template '{raw.Id}' declares no phases. Every template must contain at least one phase.");
            }

            var phases = new List<WorkflowTemplatePhase>(raw.Phases.Count);
            var phaseNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawPhase in raw.Phases)
            {
                if (string.IsNullOrWhiteSpace(rawPhase.Name))
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' has a phase with a null or blank name.");
                }

                if (!phaseNames.Add(rawPhase.Name))
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' duplicate phase name '{rawPhase.Name}'. Phase names must be unique within a template.");
                }

                if (!knownRoleIds.Contains(rawPhase.RoleId))
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' phase '{rawPhase.Name}' names role '{rawPhase.RoleId}', which is not defined in the worker-role catalog. " +
                        $"Known roles: {string.Join(", ", WorkerRoleCatalog.All.Select(r => r.Id))}.");
                }

                if (rawPhase.Inputs is null)
                {
                    throw new InvalidOperationException(
                        $"Workflow template '{raw.Id}' phase '{rawPhase.Name}' declares null inputs.");
                }

                foreach (var input in rawPhase.Inputs)
                {
                    if (!ClosedInputs.Contains(input))
                    {
                        throw new InvalidOperationException(
                            $"Workflow template '{raw.Id}' phase '{rawPhase.Name}' declares unknown input '{input}'. " +
                            $"Known inputs: {string.Join(", ", ClosedInputs)}.");
                    }
                }

                phases.Add(new WorkflowTemplatePhase(rawPhase.Name, rawPhase.RoleId, rawPhase.Instruction, rawPhase.AskFirst, rawPhase.Inputs));
            }

            templates.Add(new WorkflowTemplate(raw.Id, phases));
        }

        return templates;
    }

    private static string ResolvePath(string envVar, string overrideFileName, string defaultFileName)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var configOverride = Path.Combine(BatonPaths.Root, overrideFileName);
        return File.Exists(configOverride)
            ? configOverride
            : Path.Combine(AppContext.BaseDirectory, defaultFileName);
    }

    private static T ReadJson<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The workflow-template catalog's {what} was not found at '{path}'. The default ships next to " +
                "the engine; an override lives under BATON_HOME or the BATON_WORKFLOW_TEMPLATES_PATH env var.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The workflow-template catalog's {what} at '{path}' parsed to null.");
    }

    // Every field is [JsonRequired]: a missing member would otherwise deserialize to its default
    // (false / null) and silently ship a template nobody authored — a dropped phase or missing input constraint.
    // The catalog's contract is to fail loudly at load, so a typo'd or omitted key throws here rather than
    // surfacing at runtime.
    private sealed record RawTemplate(
        [property: JsonRequired] string Id,
        [property: JsonRequired] IReadOnlyList<RawPhase> Phases);

    private sealed record RawPhase(
        [property: JsonRequired] string Name,
        [property: JsonRequired] string RoleId,
        [property: JsonRequired] string Instruction,
        [property: JsonRequired] bool AskFirst,
        [property: JsonRequired] IReadOnlyList<string> Inputs);
}
