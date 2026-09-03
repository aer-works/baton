namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton resolve</c> (#1608), the conductor resolution surface exposed on
/// the CLI. Unlike <see cref="DecideOptions"/>, this never binds worker bindings — the room is
/// already Terminal by the time an unresolved indeterminate capture exists, so there is nothing to
/// dispatch.
/// </summary>
/// <param name="RoomDirectoryPath">An already-started room's durable state directory.</param>
/// <param name="ExecutionId">
/// The indeterminate execution this resolution targets. Optional: when omitted, the room's own
/// projected state is searched for exactly one step still awaiting resolution
/// (<see cref="ResolveCommand"/>'s own resolution logic) — the same room-level-targeting shape
/// <c>baton cancel</c>'s omitted <c>--execution</c> already uses, fail closed on zero or more than
/// one candidate.
/// </param>
/// <param name="Accept">
/// <c>true</c> for <c>--accept-capture</c>: the capture honestly satisfies its declared output(s).
/// <c>false</c> for <c>--reject</c>: the capture does not, and the step settles resolved-but-Failed
/// instead.
/// </param>
/// <param name="Reason">
/// The conductor's own justification, recorded as a room fact. Required for <c>--reject</c> and
/// <c>--close</c> (<see cref="ResolveOptionsParser"/> enforces this); optional for
/// <c>--accept-capture</c>, where the accept/reject choice already speaks for itself.
/// </param>
/// <param name="Close">
/// #1622 (d)/#1700: <c>true</c> for <c>--close</c> — the ruling for a settle shape <c>--reject</c>
/// does not admit (a <c>VerifyFailed</c>/<c>ExecutionArrested</c>/no-producer Indeterminate: no
/// captured response ever existed to accept or reject). Mutually exclusive with
/// <see cref="Accept"/> being <c>true</c>; <see cref="ResolveOptionsParser"/> enforces the three-way
/// exclusivity. <c>Accept</c> stays <c>false</c> when this is <c>true</c> — the room fact this
/// settles on (<c>FlowEvent.CaptureResolved</c>) is a not-accepted resolution either way, and
/// <c>ResolveCommand</c>/<c>MutationInterface.RecordCaptureResolutionAsync</c> are what widen which
/// producers the not-accepted path admits when this flag is set.
/// </param>
public sealed record ResolveOptions(
    string RoomDirectoryPath,
    string? ExecutionId,
    bool Accept,
    string? Reason = null,
    bool Close = false);
