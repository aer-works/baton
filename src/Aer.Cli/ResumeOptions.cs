namespace Aer.Cli;

/// <summary>
/// Parsed arguments for <c>aer resume</c> (issue #1359): the first-class "continue this worker with
/// this message" verb — re-enters an already-dispatched worker's vendor session with a new message,
/// same workspace and grants, recording the result as a new execution linked to the one it continues.
/// Mirrors <see cref="SupplyOptions"/>'s shape: a mutation command never binds a fresh snapshot
/// (§11.2), and it names its target by worker role the same way <c>aer supply</c> does.
/// </summary>
/// <param name="RoomDirectoryPath">An already-started room's durable state directory.</param>
/// <param name="Worker">
/// The worker role (<see cref="Aer.Flow.Domain.WorkflowStepDefinition.Worker"/>) to resume — the
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
/// <param name="WorkflowId">Defaults to the bound snapshot's <c>WorkflowTemplateId</c> when not given, same as <c>aer run</c>.</param>
public sealed record ResumeOptions(
    string RoomDirectoryPath,
    string Worker,
    string? Message,
    string? MessageFilePath,
    string BindingsFilePath,
    string? WorkflowId = null);
