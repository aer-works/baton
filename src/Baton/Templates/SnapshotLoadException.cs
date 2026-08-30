namespace Baton.Templates;

/// <summary>
/// Raised when a persisted <see cref="Domain.WorkflowDefinitionSnapshot"/> file fails to parse:
/// malformed JSON or an empty document. Mirrors <see cref="WorkflowDefinitionValidationException"/>'s
/// role for the frozen-snapshot half of a room's on-disk state, read back by
/// <see cref="SnapshotBinder.LoadFromFileAsync"/> when a resumed <c>baton run</c> finds a room
/// directory already bound to one.
/// </summary>
public sealed class SnapshotLoadException : BatonFlowException
{
    public SnapshotLoadException(string message)
        : base(message)
    {
    }

    public SnapshotLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
