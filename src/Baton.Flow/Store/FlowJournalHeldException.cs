namespace Baton.Flow.Store;

/// <summary>
/// Raised when opening <c>flow.jsonl</c> fails on a sharing violation — an append open (#816)
/// or, since #398, a read open. For an append, the holder is most likely a live <c>baton run</c>
/// engine driving this same room, since that command keeps its <see cref="FlowEventLogWriter"/>
/// open for the pump's whole duration rather than per call; but any sibling CLI command's own
/// transient append can lose the same race. A read open can only lose to a handle that shares
/// nothing at all — the writer's own <c>FileShare.Read</c> admits readers — e.g. a killed
/// process whose handle the OS has not finished tearing down. So neither this type nor its
/// message ever guesses who the holder is; the message carries what
/// <see cref="FileHolderProbe"/> can actually name, and otherwise only that a holder exists.
/// <para>
/// <b>Windows-only in practice:</b> the OS enforces <see cref="FileShare"/> there; .NET on Unix
/// stopped enforcing it (the .NET 6 <see cref="FileStream"/> rewrite), so on Unix the second
/// open simply succeeds and the command proceeds to ordinary validation — the crash this type
/// replaced could never arise there. Measured by the platform-forked arms in
/// <c>DecideCommandEndToEndTests</c>.
/// </para>
/// Distinct from <see cref="Baton.Flow.Concurrency.WorkflowLockedException"/>: that guards
/// <c>flow.lock</c>, which every mutation call only holds transiently, so it does not catch a
/// long-lived writer holding the journal itself.
/// </summary>
public sealed class FlowJournalHeldException : BatonFlowException
{
    public FlowJournalHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
