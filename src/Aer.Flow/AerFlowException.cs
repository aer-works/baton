namespace Aer.Flow;

/// <summary>
/// Base type for domain-level errors raised by Aer.Flow. Concrete subtypes exist per error
/// domain (e.g. <see cref="Templates.WorkflowDefinitionValidationException"/>) rather than callers
/// catching or throwing generic <see cref="InvalidOperationException"/>.
/// </summary>
public abstract class AerFlowException : Exception
{
    public string? TryInvocation { get; init; }

    protected AerFlowException(string message)
        : base(message)
    {
    }

    protected AerFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
