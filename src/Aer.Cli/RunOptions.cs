namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer run</c> (M11 Phase 3): "the CLI is the pump".
/// </summary>
/// <param name="WorkflowFilePath">
/// The <c>WorkflowDefinition</c> template file. <b>Bound</b> from only when
/// <paramref name="RoomDirectoryPath"/> has no persisted snapshot yet — a fresh start. So
/// <c>null</c> is valid for a resume-only call (M15 Phase 1, issue #137): the CLI still requires it
/// positionally (a terminal invocation names a workflow file whether fresh or resumed), but an
/// in-process caller resuming a known room directory has no reason to ask the user for one.
/// <para>
/// Resuming does still <b>read</b> it, to refuse a directory bound to a different template (#628).
/// A supplied path that does not resolve now refuses loudly with a typed
/// <c>WorkflowDefinitionValidationException</c> rather than being silently skipped (#653) — the
/// desktop no longer writes a bare template <em>id</em> here, only a real path or nothing, and
/// empty/whitespace is treated as "not supplied" at both the CLI parser and the resume check.
/// </para>
/// </param>
/// <param name="BindingsFilePath">The worker-binding config file (M11 Phase 1's sidecar shape).</param>
/// <param name="RoomDirectoryPath">
/// Where this room's durable state lives — <c>snapshot.json</c>, <c>flow.jsonl</c>, <c>artifacts/</c>,
/// <c>flow.lock</c>. Running <c>aer run</c> again against the same directory resumes it from the
/// log rather than starting over: a second invocation is how a laptop sleep or a closed
/// terminal is recovered from, not an error.
/// </param>
/// <param name="WorkflowId">
/// Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given — just a label
/// (<c>ExecutionRequest.WorkflowId</c>), not an identity a room's own directory doesn't
/// already carry.
/// </param>
/// <param name="EchoWorker">
/// When true, streams worker stdout lines live to <c>Console.Out</c> as they arrive (#882).
/// </param>
/// <param name="SettleOnVendorExhaustion">
/// 0026 §4's attended half (#1184): an <c>ExhaustedUntil</c> step settles rather than pacing itself
/// to the vendor's reset. Deliberately has no <c>aer run</c> flag and no wire field — the only
/// caller that may set it is the daemon's own interactive session turn, which reaches the pump
/// in-process, because attendedness is not something a command line or an HTTP body can attest to.
/// </param>
/// <param name="Wait">
/// #1356: the pump (<c>MutationInterface.StartWorkflowAsync</c>) already blocks in-process until it
/// returns <see cref="Aer.Flow.Domain.WorkflowStatus.Terminal"/> or <see cref="Aer.Flow.Domain.WorkflowStatus.Paused"/> —
/// this flag only changes what happens on the latter. Without it, a paused workflow returns
/// immediately (today's behaviour: nothing further to dispatch until an external <c>aer decide</c>
/// resolves it). With it, <see cref="RunCommand"/> keeps polling the room's own journal — the same
/// technique <c>aer status --follow</c> already uses — until a *different* process's decision carries
/// the workflow to Terminal, or the caller cancels. It does not reconnect to, or detect, a crashed
/// engine process from an earlier <c>aer run</c> invocation against the same room — that remains an
/// open gap (see the PR this flag shipped in).
/// </param>
/// <param name="ProjectRootDirectory">
/// The project/workspace directory this room's dispatch belongs to, recorded into the spec/baton.md §8
/// multi-project room registry (<see cref="Aer.Adapters.RoomRegistryStore"/>) alongside <paramref name="RoomDirectoryPath"/>
/// so <c>fleet_status</c> can find and group the room even when its directory sits outside every root
/// a caller happens to scan. Null resolves to the process cwd at the moment <see cref="RunCommand.ExecuteAsync"/>
/// runs — correct for a bare <c>aer run</c>, which has no separate workspace concept; <c>aer dispatch</c>
/// passes its own (possibly <c>--workspace</c>-overridden) workspace explicitly here instead, since by
/// the time <see cref="RunCommand.ExecuteAsync"/> runs the two can already differ.
/// </param>
public sealed record RunOptions(
    string? WorkflowFilePath,
    string BindingsFilePath,
    string RoomDirectoryPath,
    string? WorkflowId = null,
    bool EchoWorker = false,
    bool SettleOnVendorExhaustion = false,
    bool Wait = false,
    string? ProjectRootDirectory = null);
