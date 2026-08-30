namespace Baton;

/// <summary>
/// Base type for domain-level errors raised by Baton. Concrete subtypes exist per error
/// domain (e.g. <see cref="Templates.WorkflowDefinitionValidationException"/>) rather than callers
/// catching or throwing generic <see cref="InvalidOperationException"/>.
/// </summary>
public abstract class BatonFlowException : Exception
{
    public string? TryInvocation { get; init; }

    protected BatonFlowException(string message)
        : base(message)
    {
    }

    protected BatonFlowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
