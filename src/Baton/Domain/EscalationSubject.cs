using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// Subject of an escalation: a decisionId, a proposedOrigination, or a hostCondition.
/// The union exists because some escalations precede any engine decision object — an
/// origination proposal, or (#992) a host-observed condition like a turn watchdog timeout or
/// the dormancy breaker, which has no decision and no origination to cite. A subject must
/// always resolve to something real in the record; fabricating a DecisionId that points at
/// no decision is what this third kind exists to prevent.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Decision), "decision")]
[JsonDerivedType(typeof(ProposedOrigination), "proposedOrigination")]
[JsonDerivedType(typeof(HostCondition), "hostCondition")]
[JsonDerivedType(typeof(HeldWork), "heldWork")]
public abstract record EscalationSubject
{
    private EscalationSubject()
    {
    }

    public sealed record Decision(DecisionId DecisionId) : EscalationSubject;

    public sealed record ProposedOrigination(
        WorkflowTemplateId TemplateId,
        string? BriefRef = null) : EscalationSubject;

    /// <summary>#1001: an escalation about held work, citing the <see cref="HeldWorkRef"/> the
    /// room record already carries. The first live occupant turn proved the gap this fills:
    /// most wakes are about held work, and with no way to cite it the occupant fabricated a
    /// DecisionId instead.</summary>
    public sealed record HeldWork(HeldWorkRef Ref) : EscalationSubject;

    /// <summary>#992: a mechanical condition the room's host observed, self-describing rather
    /// than citing another record. <paramref name="Condition"/> is a stable machine-readable
    /// name (e.g. "turn-watchdog-timeout", or the dormancy breaker's
    /// <see cref="RoomEvent.TurnHostDormancyEntered.DormancyConditionName"/>);
    /// <paramref name="Detail"/> is the human-readable specifics.</summary>
    public sealed record HostCondition(string Condition, string Detail) : EscalationSubject;
}
