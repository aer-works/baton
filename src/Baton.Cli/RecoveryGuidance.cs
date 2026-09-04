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
    /// a different vendor in place, without waiting for the primary's reset. Cited by
    /// <c>StatusCommand.FormatStepStatus</c>/<c>FormatParkedStatus</c> rather than restated, per
    /// record-once.
    /// </summary>
    public const string RedispatchAdapterInstruction =
        "`baton redispatch <room-dir> --adapter <vendor>` rebinds it now";
}
