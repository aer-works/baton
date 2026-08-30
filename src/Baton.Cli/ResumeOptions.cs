namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton resume</c> (issue #1359): the first-class "continue this worker with
/// this message" verb — re-enters an already-dispatched worker's vendor session with a new message,
/// same workspace and grants, recording the result as a new execution linked to the one it continues.
/// Mirrors <see cref="SupplyOptions"/>'s shape: a mutation command never binds a fresh snapshot,
/// and it names its target by worker role the same way <c>baton supply</c> does.
/// <para>
/// Scope of the continuation claim (F7): only ONE <c>--resume</c> hop is vendor-verified
/// (<c>docs/vendor-doc-audit.md</c>'s "defer ends the query, and the session resumes" entry). A
/// resume of an already-resumed session dispatches the same way, but whether the vendor actually
/// continues from THAT resume's own turn rather than forking back to before it is not measured — see
/// that entry for the details, rather than restating them here.
/// </para>
/// </summary>
/// <param name="RoomDirectoryPath">An already-started room's durable state directory.</param>
/// <param name="Worker">
/// The worker role (<see cref="Baton.Flow.Domain.WorkflowStepDefinition.Worker"/>) to resume — the
/// same name a bindings file keys the worker's entry under.
/// </param>
/// <param name="Message">
/// The literal message text to continue the worker's session with. Mutually exclusive with
/// <paramref name="MessageFilePath"/> — exactly one is required.
/// </param>
/// <param name="MessageFilePath">
/// A file whose full contents are the message to continue the worker's session with — for a message
/// too long, or too awkward, to pass as a single shell argument. Mutually exclusive with
/// <paramref name="Message"/> — exactly one is required.
/// </param>
/// <param name="BindingsFilePath">
/// The worker-binding config file — must name the same bindings the room was originally dispatched
/// with; the worker's own recorded <c>SessionId</c> is what makes a resume possible at all.
/// </param>
/// <param name="WorkflowId">Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given, same as <c>baton run</c>.</param>
public sealed record ResumeOptions(
    string RoomDirectoryPath,
    string Worker,
    string? Message,
    string? MessageFilePath,
    string BindingsFilePath,
    string? WorkflowId = null);
