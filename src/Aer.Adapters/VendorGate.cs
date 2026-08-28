namespace Aer.Adapters;

/// <summary>
/// The vendor-specific mechanisms that make decision 0029's mandatory <c>PreToolUse</c> gate fire on
/// a spawned process, separated from the rest of an invocation so a caller that is not building a
/// Flow dispatch can still install them (#703).
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither vendor's gate is reachable from the environment</b>, which is why this carries
/// <see cref="Args"/> at all rather than being a variable set. <c>claude</c> loads the hook only from
/// a <c>--settings</c> path on argv; <c>agy</c> discovers it only from <c>.agents/hooks.json</c>
/// inside a directory handed to <c>--add-dir</c>. A design that injects environment variables and
/// expects a gate is building an ungated worker — that was the first shape proposed for #703, and it
/// was abandoned on these two facts rather than on preference.
/// </para>
/// <para>
/// Deliberately NOT the whole of an adapter's <c>Resolve</c>. That output additionally carries
/// <c>%AER_ARTIFACTS_ROOT%</c>-style references only <c>CoreDispatcher</c> expands, and a prompt
/// built around Flow's <c>AER_INPUT_&lt;n&gt;</c>/<c>AER_OUTPUT_DIR</c> convention that has no meaning
/// for a per-turn call inside another worker's process. Reusing it wholesale would hand a vendor CLI
/// unexpanded placeholders as literal arguments.
/// </para>
/// </remarks>
/// <param name="Args">Arguments to append to the invocation, in order.</param>
/// <param name="Environment">
/// Variables to set on the spawned process, overwriting any inherited value of the same name — a
/// gate value AER computed losing to an operator's shell is the failure this exists to stop.
/// </param>
public sealed record VendorGate(
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>
    /// The gate for a vendor, or <see langword="null"/> if AER ships no gate for it — which a caller
    /// must treat as "this process cannot be gated", never as "no gate needed".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed on the known vendor names (<c>"claude"</c>, <c>"agy"</c>). An
    /// unknown vendor returning null rather than an empty gate is the fail-closed direction: an empty
    /// <see cref="VendorGate"/> is indistinguishable from a real one that happened to need no
    /// arguments, and would let a caller believe it had installed something.
    /// </para>
    /// <para>
    /// <paramref name="workspace"/> is the directory a granted write may land in besides the outbox
    /// — <c>WorkerEnvironment.WorkspaceVariable</c>. <b>Passing null is a real narrowing, not a
    /// default:</b> per <c>HookCheckCommand.Execute</c>, a null workspace narrows a granted write to
    /// the outbox only, so a participant granted <c>WriteFiles</c> gets every workspace write refused
    /// by the hook with no configuration anywhere explaining why. The first version of this omitted
    /// the parameter entirely, which made every dialogue participant silently narrower than the same
    /// vendor on the dispatch path; a reviewer found it because <c>AssertGateIsInstalled</c> checked
    /// only <c>gate ⊆ Resolve</c>, the direction that cannot see an omission.
    /// </para>
    /// </remarks>
    public static VendorGate? For(string vendor, PermissionGrant? grant, string? workspace = null) => vendor switch
    {
        "claude" => ClaudeWorkerAdapter.BuildGate(grant, workspace),
        "agy" => AgyWorkerAdapter.BuildGate(grant, workspace),
        _ => null,
    };
}
