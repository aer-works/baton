using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;
using Baton.Status;

namespace Baton.Vendors;

/// <summary>
/// The volatile half of a worker role (#888): which vendor/model/effort actually runs it. Lives in
/// <c>WorkerTiers.json</c>, separate from the roles, so swapping a model is one edit that every role
/// on the tier inherits — and, because the catalog is read at runtime rather than embedded, that edit
/// needs no rebuild (drop a <c>worker-tiers.json</c> under <see cref="BatonPaths.Root"/>, or point
/// <see cref="WorkerRoleCatalog.TiersPathEnvironmentVariable"/> at one).
/// </summary>
public sealed record WorkerTier([property: JsonRequired] string Adapter, string? Model, string? Effort);

/// <summary>
/// A composable worker-role profile — the building block the front door (#887) composes into
/// workflows. The <b>stable</b> half (grant, timeout, verdict, purpose) is authored in
/// <c>WorkerRoles.json</c>; the <b>volatile</b> half (<see cref="Adapter"/>/<see cref="Model"/>/
/// <see cref="Effort"/>) is resolved from the role's <see cref="Tier"/> in <c>WorkerTiers.json</c>.
/// A role never names a vendor or model directly, so a model swap never touches a role's capability.
/// </summary>
public sealed record WorkerRole(
    string Id,
    string Tier,
    string Adapter,
    string? Model,
    string? Effort,
    PermissionGrant Grant,
    TimeSpan Timeout,
    bool ProducesVerdict,
    string Purpose,
    IReadOnlyList<WorkerRoleOutput> Outputs);

/// <summary>
/// One file a role's dispatch produces in <c>BATON_OUTPUT_DIR</c> (#897) — the structured, per-role
/// form of what <c>tools/baton-agy-loop/dispatch.py</c> spells out inline today. The front door (#887)
/// declares it as a <see cref="ProducedOutput"/> the engine's <c>ContractValidator</c> checks, and
/// appends <see cref="Instruction"/> to the dispatch prompt so the worker is told to produce exactly
/// what the contract asserts — the name, the shape, and the instruction single-sourced here so a spec
/// prompt stays just the task.
/// </summary>
/// <param name="Schema">
/// The document shape the file must parse as — <see cref="OutputSchema.None"/> for existence-only, or
/// <see cref="OutputSchema.ReviewVerdict"/> for a schema-checked verdict (decision 0043, parse-only).
/// </param>
public sealed record WorkerRoleOutput(string Name, OutputSchema Schema, string Instruction);

/// <summary>
/// The single, shared worker-role catalog — the same <c>WorkerRoles.json</c>/<c>WorkerTiers.json</c>
/// that <c>tools/baton-agy-loop/dispatch.py</c> reads (#888, the #836 shared-source pattern). Read at
/// runtime, never embedded, so the operator can retune tiers without a rebuild.
/// </summary>
/// <remarks>
/// Resolution order per file, evaluated fresh on every access (the same "resolve, never capture"
/// discipline <see cref="BatonPaths"/> keeps, so a test or a live edit is honoured immediately):
/// <list type="number">
/// <item>the <c>BATON_WORKER_*_PATH</c> environment override, when set — for a one-off experiment;</item>
/// <item><c>{BatonPaths.Root}/worker-tiers.json</c> (or <c>worker-roles.json</c>) when it exists — the
///   operator's durable, rebuild-free override;</item>
/// <item>the default shipped next to the assembly (<see cref="AppContext.BaseDirectory"/>).</item>
/// </list>
/// Tiers and roles resolve independently, so overriding a model does not freeze the role definitions.
/// </remarks>
public static class WorkerRoleCatalog
{
    public const string TiersPathEnvironmentVariable = "BATON_WORKER_TIERS_PATH";
    public const string RolesPathEnvironmentVariable = "BATON_WORKER_ROLES_PATH";

    private const string TiersDefaultFileName = "WorkerTiers.json";
    private const string RolesDefaultFileName = "WorkerRoles.json";
    private const string TiersOverrideFileName = "worker-tiers.json";
    private const string RolesOverrideFileName = "worker-roles.json";

    // Plain JSON only — no comments, no trailing commas. dispatch.py reads the same two files through
    // stdlib json.loads, which tolerates neither; matching that here keeps "one shared source" a real
    // guarantee rather than a file only the C# side can parse (#888 finding).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Every role in the catalog, resolved against the current tiers, in file order.</summary>
    public static IReadOnlyList<WorkerRole> All => Load();

    /// <summary>The role with <paramref name="id"/>, or throws if the catalog has no such role.</summary>
    public static WorkerRole For(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException(
                $"No worker role '{id}' in the catalog. Known roles: {string.Join(", ", All.Select(r => r.Id))}.");
    }

    private static IReadOnlyList<WorkerRole> Load()
    {
        var tiers = ReadJson<Dictionary<string, WorkerTier>>(
            ResolvePath(TiersPathEnvironmentVariable, TiersOverrideFileName, TiersDefaultFileName), "tier map");
        var rawRoles = ReadJson<List<RawRole>>(
            ResolvePath(RolesPathEnvironmentVariable, RolesOverrideFileName, RolesDefaultFileName), "role list");

        if (rawRoles.Count == 0)
        {
            throw new InvalidOperationException("The worker-role catalog is empty.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roles = new List<WorkerRole>(rawRoles.Count);
        foreach (var raw in rawRoles)
        {
            if (!seen.Add(raw.Id))
            {
                throw new InvalidOperationException($"Duplicate worker role id '{raw.Id}' in the catalog.");
            }

            if (!tiers.TryGetValue(raw.Tier, out var tier))
            {
                throw new InvalidOperationException(
                    $"Worker role '{raw.Id}' names tier '{raw.Tier}', which is not defined in the tier map. " +
                    $"Known tiers: {string.Join(", ", tiers.Keys)}.");
            }

            // [JsonRequired] guarantees the `outputs` key is present, not that it is non-null or
            // non-empty. Both would ship a role that declares nothing — silently defeating the floor
            // every role's primary output exists to be (a worker that writes nothing fails loudly).
            // Named like every other guard here, unlike the bare ArgumentNullException a null Outputs
            // would otherwise throw out of the Select below.
            if (raw.Outputs is null || raw.Outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Worker role '{raw.Id}' declares no outputs. Every role declares at least one output " +
                    "file the worker writes to BATON_OUTPUT_DIR — the floor that catches a silent no-op.");
            }

            roles.Add(new WorkerRole(
                Id: raw.Id,
                Tier: raw.Tier,
                Adapter: tier.Adapter,
                Model: tier.Model,
                Effort: tier.Effort,
                Grant: new PermissionGrant(
                    ReadFiles: raw.ReadFiles,
                    WriteFiles: raw.WriteFiles,
                    RunShellCommands: raw.RunShellCommands,
                    ShellCommandPatterns: raw.ShellCommandPatterns,
                    NetworkAccess: raw.NetworkAccess,
                    DeniedShellCommandPatterns: raw.DeniedShellCommandPatterns,
                    ShellCommandsAreReadOnly: raw.ShellCommandsAreReadOnly),
                Timeout: TimeSpan.FromMinutes(raw.TimeoutMinutes),
                ProducesVerdict: raw.VerdictSchema,
                Purpose: raw.Purpose,
                Outputs: raw.Outputs.Select(o => ResolveOutput(raw.Id, o)).ToList()));
        }

        return roles;
    }

    private static WorkerRoleOutput ResolveOutput(string roleId, RawOutput raw)
    {
        // Reject a '.'-prefixed name at load, mirroring ProducedOutput's own constructor (see
        // ReservedOutputNames for why the namespace is reserved). Without this the invalid name would
        // sail through the catalog and only throw when the front door converts it to a ProducedOutput
        // at dispatch — the exact "fail at dispatch, not at load" this catalog exists to prevent.
        if (ReservedOutputNames.IsReserved(raw.Name))
        {
            throw new InvalidOperationException(
                $"Worker role '{roleId}' output name '{raw.Name}' is invalid: {ReservedOutputNames.RejectionClause}.");
        }

        // Mapped explicitly rather than deserialized straight into OutputSchema: the catalog's wire
        // form is snake_case (dispatch.py reads the same file), and an unknown value must fail loudly
        // at load — a silently-defaulted OutputSchema.None would drop a verdict's schema check and
        // pass a file that is not a verdict, the exact false capability the RawRole discipline forbids.
        var schema = raw.Schema switch
        {
            "none" => OutputSchema.None,
            "review_verdict" => OutputSchema.ReviewVerdict,
            "diff" => OutputSchema.Diff,
            _ => throw new InvalidOperationException(
                $"Worker role '{roleId}' output '{raw.Name}' declares unknown schema '{raw.Schema}'. " +
                "Known schemas: none, review_verdict, diff."),
        };

        return new WorkerRoleOutput(raw.Name, schema, raw.Instruction);
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
                $"The worker-role catalog's {what} was not found at '{path}'. The default ships next to " +
                "the engine; an override lives under BATON_HOME or the BATON_WORKER_*_PATH env var.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"The worker-role catalog's {what} at '{path}' parsed to null.");
    }

    // Every field is [JsonRequired]: a missing member would otherwise deserialize to its default
    // (false / 0 / null) and silently ship a role nobody authored — a false capability, an
    // instant-timeout, a dropped verdict schema. The catalog's contract is to fail loudly at load, so
    // a typo'd or omitted key throws here rather than surfacing at dispatch time (#888 finding).
    private sealed record RawRole(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Tier,
        [property: JsonRequired] bool ReadFiles,
        [property: JsonRequired] bool WriteFiles,
        [property: JsonRequired] bool RunShellCommands,
        [property: JsonRequired] bool NetworkAccess,
        [property: JsonRequired] int TimeoutMinutes,
        [property: JsonRequired] bool VerdictSchema,
        [property: JsonRequired] string Purpose,
        [property: JsonRequired] IReadOnlyList<RawOutput> Outputs,
        // Optional, unlike every field above: most roles never scope RunShellCommands and omitting
        // these three is exactly "no patterns / not asserted read-only" — the same default
        // PermissionGrant's own constructor already carries (#1456, spec/baton.md §9). Making them
        // [JsonRequired] would force every existing role in the catalog to grow dead keys for a
        // capability only `review` uses.
        IReadOnlyList<string>? ShellCommandPatterns = null,
        IReadOnlyList<string>? DeniedShellCommandPatterns = null,
        bool ShellCommandsAreReadOnly = false);

    private sealed record RawOutput(
        [property: JsonRequired] string Name,
        [property: JsonRequired] string Schema,
        [property: JsonRequired] string Instruction);
}
