namespace Aer.Flow.Mutation;

/// <summary>
/// Raised when <c>aer resume</c> (issue #1359) is asked to continue a worker that cannot be
/// continued right now: no step is bound to the named worker role, more than one step names it (an
/// ambiguous target this verb refuses rather than guesses), the target step has no execution yet
/// (<see cref="Domain.StepStatus.Pending"/>), the target step's latest attempt is still
/// <see cref="Domain.StepStatus.Running"/> (mid-flight steering is #1359's explicitly excluded scope
/// — only a terminal or stalled worker is a resume target), or the resolved binding is not a
/// <see cref="WorkerBinding.Process"/> (a non-process worker has no vendor session to resume).
/// Nothing is appended to the log when this is thrown.
/// </summary>
public sealed class InvalidResumeException : AerFlowException
{
    public InvalidResumeException(string message)
        : base(message)
    {
    }
}
