namespace Aer.Flow.Mutation;

/// <summary>
/// Raised when a <c>Worker</c> role name has no corresponding <see cref="WorkerBinding"/> among
/// those supplied to <see cref="MutationInterface.StartWorkflowAsync"/> — or resolves to one of the
/// wrong kind, e.g. a step-less supplementary execution naming a
/// <see cref="WorkerBinding.Process"/> role, which by definition must be non-process. Distinct from
/// a <c>WorkflowDefinition</c> validation error — the template itself is well-formed (the spec
/// says nothing about which workers exist), this is a caller configuration gap discovered only at
/// dispatch time.
/// </summary>
public sealed class UnresolvedWorkerException : AerFlowException
{
    public UnresolvedWorkerException(string message)
        : base(message)
    {
    }

    public UnresolvedWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
