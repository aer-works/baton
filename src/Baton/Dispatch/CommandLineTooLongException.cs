namespace Baton.Dispatch;

/// <summary>
/// Raised when the command line a dispatch would assemble is longer than the host OS will accept,
/// caught by <see cref="CoreDispatcher"/> before it ever reaches <c>BatonTask</c> (#598). Both worker
/// adapters embed the whole prompt inline as a single argument
/// (<c>AgyWorkerAdapter</c>'s <c>["-p", prompt]</c>, <c>ClaudeWorkerAdapter</c>'s <c>"-p", prompt</c>),
/// so a long enough prompt hits a limit that has nothing to do with the prompt being wrong.
/// <para>
/// This exists to name the failure. Without it the spawn is attempted and fails inside
/// <c>BatonTask</c>, which maps every spawn error alike to <c>BatonErrorCode.SpawnFailed</c> —
/// surfacing to the operator as an OS-authored message about a filename being too long, naming
/// neither the prompt, its size, nor
/// the limit it crossed. <c>Baton.Cli</c>'s top-level <c>catch (BatonFlowException)</c> renders this one
/// as an ordinary AER error instead.
/// </para>
/// <para>
/// <b>A recorded outcome since #747, reversing this doc's earlier position.</b> Caught in
/// <c>MutationInterface.DispatchAndRecordOutcomeAsync</c> and recorded as <c>ExecutionFailed</c>
/// with <c>FailureClassification.Permanent</c>, carrying the refusal message. The earlier doc
/// extended the safe crash state (intent recorded, nothing ran, recover by re-submission)
/// to this refusal — but that re-submission story only operates across a restart, and a
/// deterministic refusal re-refuses on every re-submission, so "recoverable" could never
/// complete. The measurement behind this — four live refusals, each leaving a log ending at
/// <c>ExecutionRequestAccepted</c>, indistinguishable from a healthy run — is recorded on #747.
/// The crash state itself is unchanged and still safe, and the generic-refusal arm
/// (<c>BatonException</c>, Retryable) is the same treatment for the family members no typed guard
/// names.
/// </para>
/// </summary>
public sealed class CommandLineTooLongException : BatonFlowException
{
    public CommandLineTooLongException(string message)
        : base(message)
    {
    }

    public CommandLineTooLongException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
