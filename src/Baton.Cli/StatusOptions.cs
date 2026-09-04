namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton status</c> — see <see cref="StatusCommand"/> for what the command
/// does. Unlike every other command in this namespace, this takes no <c>--bindings</c> file at all
/// (it never resolves a worker binding) and no <c>--workflow-id</c> (nothing here dispatches, so
/// there is nothing to label).
/// </summary>
/// <param name="RoomDirectoryPath">
/// record-once-ok: #443 src/Baton.Cli/CancelOptions.cs
/// An already-started room's durable state directory. <c>baton status</c> never binds a fresh
/// snapshot the way <c>baton run</c> does — it only ever reads one that already exists.
/// </param>
/// <param name="Follow">
/// When set, keep polling <c>flow.jsonl</c> for new events after printing the current state,
/// printing each as it lands, until the workflow reaches a terminal state or the caller cancels.
/// </param>
/// <param name="Json">
/// #1356: emit one <see cref="WorkflowStatusView"/> JSON object to stdout instead of the human
/// rendering — nothing else on stdout in this mode. Incompatible with <paramref name="Follow"/>
/// (refused by the parser): a follow loop's whole point is a running commentary, which is exactly
/// the "parseable, single object" contract this flag promises.
/// </param>
/// <param name="RepoPath">
/// <c>--repo</c> (#1645) — see <see cref="DispatchOptions.RepoPath"/>'s doc for the shared contract;
/// <see cref="InstalledVersionDrift"/> is the one evaluator both commands call.
/// </param>
public sealed record StatusOptions(
    string RoomDirectoryPath, bool Follow = false, bool Json = false, string? RepoPath = null);
