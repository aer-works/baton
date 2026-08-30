namespace Baton.Flow.Mutation;

/// <summary>
/// Raised when a candidate <see cref="Domain.FlowEvent.ExternalDecisionRecorded"/> violates one of the
/// closed-set validation rules: the referenced execution is not currently paused,
/// <c>TargetStepId</c> is present without <see cref="Domain.DecisionType.Supersede"/> (or absent
/// with it, or outside the pause point's declared <c>SupersedeTargets</c>), a
/// <see cref="Domain.DecisionType.Supersede"/> target has not itself succeeded,
/// <c>SupplementaryExecutionId</c> is missing for <see cref="Domain.DecisionType.Supersede"/> or
/// does not name a recorded successful execution, or <see cref="Domain.DecisionType.RetryWithRevision"/>
/// targets a step that has already succeeded. Rejected, never silently widened — nothing is
/// appended to the log when this is thrown.
/// </summary>
public sealed class InvalidExternalDecisionException : BatonFlowException
{
    public InvalidExternalDecisionException(string message)
        : base(message)
    {
    }
}
