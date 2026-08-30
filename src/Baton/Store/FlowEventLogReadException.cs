namespace Baton.Store;

/// <summary>
/// Raised when a complete, newline-terminated line in <c>flow.jsonl</c> does not deserialize to a
/// known <see cref="Domain.FlowEvent"/>. A malformed complete line is a corruption of the source of
/// truth — never silently skipped — as distinct from a torn trailing line, which is
/// simply not yet a complete event and is excluded without error.
/// </summary>
public sealed class FlowEventLogReadException : BatonFlowException
{
    public FlowEventLogReadException(string message)
        : base(message)
    {
    }

    public FlowEventLogReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
