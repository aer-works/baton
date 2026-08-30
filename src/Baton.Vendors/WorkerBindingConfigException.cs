using Baton.Flow;

namespace Baton.Vendors;

/// <summary>
/// Raised when a candidate worker-binding config fails to parse or fails structural validation:
/// malformed JSON, an empty document, or an entry missing a required field. Mirrors
/// <c>Baton.Flow.Templates.WorkflowDefinitionValidationException</c>'s role for the workflow template
/// half of the same "config that shapes a run" family.
/// </summary>
public sealed class WorkerBindingConfigException : BatonFlowException
{
    public WorkerBindingConfigException(string message)
        : base(message)
    {
    }

    public WorkerBindingConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
