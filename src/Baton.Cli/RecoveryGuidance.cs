namespace Baton.Cli;

/// <summary>
/// The one instruction for recovering a room whose scheduling engine died mid-wait — an in-process
/// retry deferral (most visibly a vendor-quota park) outliving the <c>baton run</c> pump that
/// scheduled it, so nothing anywhere will ever revisit the room on its own (#1586). <c>baton run</c>
/// against the room's own <c>workflow.json</c>/<c>bindings.json</c> with <c>--room-dir</c> pointed at
/// it is the one verb that re-enters the exact wait the dead pump was in (spec/baton.md §3) —
/// <c>StatusCommand.FormatParkedStatus</c> (#1582) said so first; every other refusing verb
/// (<c>RedispatchCommand</c>, <c>CancelCommand</c>) cites this constant instead of restating the
/// wording, per record-once.
/// </summary>
internal static class RecoveryGuidance
{
    public const string RunRoomDirInstruction =
        "re-run `baton run` against this room's own workflow.json and bindings.json with --room-dir pointed at it";

    /// <summary>
    /// #802: the operator verb a vendor-quota park with no declared fallback is waiting on —
    /// <c>RedispatchCommand</c>'s already-shipped <c>--adapter</c> flag rebinds the parked step onto
    /// a different vendor in place, without waiting for the primary's reset. Valid only once the
    /// room is (or is about to be) terminal — <c>RedispatchCommand</c> refuses any parent room
    /// without a terminal sentinel, which a still-alive owning engine has not written yet; use
    /// <see cref="CancelThenRedispatchAdapterInstruction"/> for that case instead (#1838). Cited by
    /// <c>StatusCommand.FormatStepStatus</c>/<c>FormatParkedStatus</c> rather than restated, per
    /// record-once.
    /// </summary>
    public const string RedispatchAdapterInstruction =
        "`baton redispatch <room-dir> --adapter <vendor>` rebinds it now";

    /// <summary>
    /// #1838: the no-fallback park's owning engine is still alive (<c>EngineLivenessProbe</c> read
    /// <c>Alive</c>) — <c>RedispatchCommand</c> refuses any room without a terminal sentinel
    /// (RedispatchCommand.cs), and a live, still-pumping <c>baton run</c> has not written one, so
    /// naming redispatch alone sends the operator into that refusal. <c>baton cancel</c> settles the
    /// room to terminal first (CancelCommand.cs's quota-parked-step arrest path); only then does
    /// redispatch work. Cited by <c>StatusCommand.FormatParkedStatus</c> rather than restated, per
    /// record-once.
    /// </summary>
    public const string CancelThenRedispatchAdapterInstruction =
        "`baton cancel <room-dir>`, then `baton redispatch <room-dir> --adapter <vendor>`, rebinds it";
}
