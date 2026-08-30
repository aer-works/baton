using Baton.Flow.Domain;

namespace Baton.Flow.Templates;

/// <summary>
/// Raised when a candidate <see cref="WorkflowDefinition"/> fails to parse or fails structural
/// validation: malformed JSON, duplicate <see cref="StepId"/>s, a <c>DependsOn</c>
/// reference to an undeclared <see cref="StepId"/>, a cyclic <c>DependsOn</c> graph, or a
/// <c>SupersedeTargets</c> entry that is not a transitive ancestor of the declaring step.
/// Carries every violation found, not just the first, so a caller can fix a template in one pass.
/// </summary>
public sealed class WorkflowDefinitionValidationException : BatonFlowException
{
    public IReadOnlyList<string> Errors { get; }

    public WorkflowDefinitionValidationException(IReadOnlyList<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    public WorkflowDefinitionValidationException(IReadOnlyList<string> errors, Exception innerException)
        : base(BuildMessage(errors), innerException)
    {
        Errors = errors;
    }

    private static string BuildMessage(IReadOnlyList<string> errors)
        => $"WorkflowDefinition is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
}
