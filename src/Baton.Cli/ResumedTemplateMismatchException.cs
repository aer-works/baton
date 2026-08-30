using Baton;

namespace Baton.Cli;

/// <summary>
/// Raised by <see cref="RunCommand.ExecuteAsync"/> when the room directory already holds a bound
/// snapshot and the workflow file named on the command line is a different template (#628).
/// </summary>
/// <remarks>
/// <para>
/// Resuming from the snapshot rather than the named file is intended (M15 Phase 1, #137): a second
/// <c>baton run</c> against the same room directory is how a closed terminal or a slept laptop is
/// recovered from. What was not intended is that it happened silently even when the two disagreed,
/// so an operator who pointed a fresh workflow at a directory another room had used got that other
/// room's result — down to its declared outputs and timeout — with no new events written and no
/// indication that the file they named was never read.
/// </para>
/// <para>
/// A differing <c>WorkflowTemplateId</c> is the case where the operator has demonstrably asked for
/// different work than the directory is bound to, which is why it is refused rather than reported.
/// A matching one still resumes; <see cref="CommandResult.ResumedFromSnapshot"/> is what says so.
/// </para>
/// </remarks>
public sealed class ResumedTemplateMismatchException : BatonFlowException
{
    public string BoundTemplateId { get; }

    public string NamedTemplateId { get; }

    public string RoomDirectoryPath { get; }

    public ResumedTemplateMismatchException(
        string boundTemplateId, string namedTemplateId, string roomDirectoryPath)
        : base(
            $"Room directory '{roomDirectoryPath}' is already bound to workflow template " +
            $"'{boundTemplateId}', but the workflow file given names '{namedTemplateId}'. Resuming " +
            "would run the bound template and report its result, not the one asked for. Use a fresh " +
            $"room directory for '{namedTemplateId}', or pass the template the directory is bound to.")
    {
        BoundTemplateId = boundTemplateId;
        NamedTemplateId = namedTemplateId;
        RoomDirectoryPath = roomDirectoryPath;
    }
}
