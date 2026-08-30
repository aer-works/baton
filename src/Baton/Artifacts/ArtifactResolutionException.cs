namespace Baton.Artifacts;

/// <summary>
/// Raised when the <see cref="ArtifactManager"/> cannot resolve a step's declared input to a
/// producing dependency's output, or that dependency has no successful execution yet to resolve
/// against. Should not occur for a step the Dependency Resolver has already deemed
/// ready — condition 1 guarantees every <c>DependsOn</c> entry has succeeded — so this signals
/// either a malformed <c>WorkflowDefinition</c> (an input name no declared dependency produces) or
/// a caller invoking artifact resolution outside that guarantee.
/// </summary>
public sealed class ArtifactResolutionException : BatonFlowException
{
    public ArtifactResolutionException(string message)
        : base(message)
    {
    }

    public ArtifactResolutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
