// GENERATED FILE — DO NOT EDIT.
// Source: design/interaction-states.json
// Regenerate: pixi run tokens
//
// Hand edits are reverted by the next regeneration and fail CI in the meantime
// (Aer.Architecture.Tests). Change the register file instead.

namespace Aer.Ui.Core;

/// <summary>
/// The interaction states — the situations every surface must handle (#616; ratified
/// thirteen on #495). A different population from <see cref="AerStatus"/>: that is the
/// room-lifecycle vocabulary, this is the screen-situation inventory; they overlap at
/// record-once-ok: #443 design/interaction-states.json
/// Cancelled/Failed only. 0020's rules govern consumption: rendering is a projection,
/// absence is not a state — which is why the presentation methods below throw on an
/// unmapped member instead of answering with a default.
/// </summary>
public enum InteractionState
{
    Empty,
    Loading,
    Disconnected,
    WorkerMissing,
    FolderGone,
    Cancelled,
    Failed,
    Archived,
    LongOutput,
    ReducedMotion,
    GateUnverified,
    WaitingOnLock,
    Dormant,
    OutOfPlan,
}

public static class InteractionStatePresentation
{
    /// <summary>The state's display name, as the register records it.</summary>
    public static string DisplayName(this InteractionState state) => state switch
    {
        InteractionState.Empty => "Empty",
        InteractionState.Loading => "Loading",
        InteractionState.Disconnected => "Disconnected",
        InteractionState.WorkerMissing => "Worker missing",
        InteractionState.FolderGone => "Folder gone",
        InteractionState.Cancelled => "Cancelled",
        InteractionState.Failed => "Failed",
        InteractionState.Archived => "Archived",
        InteractionState.LongOutput => "Long output",
        InteractionState.ReducedMotion => "Reduced motion",
        InteractionState.GateUnverified => "Gate unverified",
        InteractionState.WaitingOnLock => "Waiting on another room's lock",
        InteractionState.Dormant => "Dormant",
        InteractionState.OutOfPlan => "Out of plan",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped interaction state."),
    };

    /// <summary>What a surface holding this state does — the register's behaviour sentence.</summary>
    public static string Behaviour(this InteractionState state) => state switch
    {
        InteractionState.Empty => "Says what would be here and offers the one action that creates it. Never a bare \"no items\".",
        InteractionState.Loading => "Keeps the previous content and marks it stale rather than blanking. A list that empties itself while refreshing reads as data loss.",
        InteractionState.Disconnected => "The phone says so at the top, keeps showing the last known rooms marked stale, and queues what you type. Work continues on the computer regardless — that is the point of the daemon owning the run.",
        InteractionState.WorkerMissing => "A room whose vendor CLI is gone says which one and how to fix it. It is not a failure of the room.",
        InteractionState.FolderGone => "The room is greyed and marked unavailable, never an error dialog, and never silently dropped from the list.",
        InteractionState.Cancelled => "Reads as cancelled — a distinct state with its own mark, never collapsed into finished.",
        InteractionState.Failed => "Reads as failed, shows the error text in place, and offers the failing worker as the first way to fix it.",
        InteractionState.Archived => "Out of the default list, still searchable, restorable in one action.",
        InteractionState.LongOutput => "Truncated with an explicit \"showing first N lines\" and a way to see all of it. Never silently cut.",
        InteractionState.ReducedMotion => "Every animated state degrades to a correct still frame — the working mark is a spinner's static frame by design, not an absence.",
        InteractionState.GateUnverified => "A worker whose permission mechanism could not be confirmed working at start says so before any tool runs, rather than silently rendering a gate that might never fire — a broken hook or a disabled callback both look exactly like a working one otherwise.",
        InteractionState.WaitingOnLock => "Reads as a wait, never as an error and never as generic working: names the room that holds this folder, linked, so the choice — wait, or go there — is discoverable. Opening a second room on a folder that already has one warns first; legal, but a choice made knowingly.",
        InteractionState.Dormant => "The room stopped machine turns after repeated turns that committed nothing, and says so in the transcript with the reason and the wake control. A message to a dormant room is answered with this state — waking is your explicit action, never a side effect of asking how it's going.",
        InteractionState.OutOfPlan => "Displays quota/subscription exhaustion with its reset time when known (\"Out of plan — resumes {local time}\") or an explicit unknown (\"Out of plan — reset unknown\"), distinct from failure.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped interaction state."),
    };
}
