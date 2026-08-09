using Aer.Adapters;
using Aer.Flow.Projection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aer.Ui.Core;

/// <summary>
/// Records the operator's answer to a runtime permission for <paramref name="permissionRequestId"/>,
/// as one of <see cref="PermissionDecisionKind"/>'s constants (0022's ladder), carrying the optional
/// <paramref name="reason"/> a denial hands back to the worker. The receiving end (<c>MainWindow</c>)
/// owns the <c>POST /api/rooms/permissions/answer</c> round trip this implies — the same skin-side
/// inversion <see cref="DecideDelegate"/> uses for step decisions, keeping this assembly Avalonia-free.
/// </summary>
public delegate Task AnswerPermissionDelegate(string permissionRequestId, string decisionKind, string? reason);

/// <summary>
/// The conversational permission gate 0022 draws (M-Phase-6 #390): one pending permission surfaced
/// inline in the chat, with the scope ladder offered at the moment of asking rather than buried in
/// settings. Rebuilt from <see cref="RoomProjection"/>'s <see cref="PendingPermission"/> on every
/// load/refresh (a projected fact, not retained handler state — the "re-derived, not remembered"
/// discipline the rest of the window's rendering follows), so a permission that dies with its turn
/// (0022 §5) vanishes on the next projection that no longer carries it.
/// </summary>
/// <remarks>
/// <para>
/// The cross-room rung ("any <em>this command</em> in any room") is deliberately absent: 0052 holds
/// it until a project-scoped store exists, so offering it here would promise persistence the engine
/// cannot keep (<see cref="RuntimePermissionGrantAmender"/> no-ops it to once-only). The rungs shown
/// are exactly the ones the amender persists plus the two once-only answers.
/// </para>
/// <para>
/// The command-family rungs (<see cref="AllowCommandInRoomCommand"/>, <see cref="DenyAlwaysCommand"/>)
/// render only when a family can be derived from the ask (<see cref="HasCommandScope"/>) — the same
/// fail-closed the amender does when a command line opens with a shell metacharacter, surfaced as
/// "the scoped rung isn't offered" rather than offered-then-silently-narrowed-to-once.
/// </para>
/// <para>
/// <b>Keyboard (view concern, not enforced here):</b> <c>y</c> answers <see cref="AllowOnceCommand"/>
/// and <c>n</c> answers <see cref="DenyCommand"/>; neither is ever bound to <c>Enter</c> (0022 §4,
/// #481). This type exposes the two commands; the view binds the keys, and a Ui.Core test cannot
/// prove the Enter exclusion — that is the harness's job.
/// </para>
/// </remarks>
public sealed partial class PendingPermissionViewModel : ObservableObject
{
    private readonly AnswerPermissionDelegate _answer;

    public string PermissionRequestId { get; }

    /// <summary>The plain-language ask, e.g. "claude wants to run: rm -rf build/" — or, for a non-shell tool, "claude wants to use Edit".</summary>
    public string PromptText { get; }

    /// <summary>
    /// The asked command's family (e.g. "rm"), or <see langword="null"/> when the ask isn't a shell
    /// tool or its command line can't be read/scoped safely. <see cref="HasCommandScope"/> gates the
    /// two command-family rungs on this.
    /// </summary>
    public string? CommandFamily { get; }

    /// <summary>Whether the command-family rungs are offered — false hides them rather than offering a scope that would silently fall back to once-only.</summary>
    public bool HasCommandScope => CommandFamily is not null;

    /// <summary>"Allow rm in this room" — meaningful only when <see cref="HasCommandScope"/>.</summary>
    public string AllowCommandInRoomLabel => $"Allow {CommandFamily} in this room";

    /// <summary>"Always deny rm" — the standing-refusal rung's label; meaningful only when <see cref="HasCommandScope"/>.</summary>
    public string DenyAlwaysLabel => $"Always deny {CommandFamily}";

    /// <summary>
    /// The honesty clause under the room-ceiling rung: granting "any command in this room" grants the
    /// shell, which reaches files and the network regardless. One wording home
    /// (<see cref="PermissionGrantWording"/>) shared with the bindings editor's bind-time refusal so the
    /// two can't drift on what the shell defeats.
    /// </summary>
    public string AllowRoomDisclosure => PermissionGrantWording.RoomShellGrantReaches();

    /// <summary>
    /// Whether the rungs may be answered — false while the UI's pump holds the room lock for any
    /// mutation (this answer or another), driven by <see cref="MainWindowViewModel.IsMutationInFlight"/>,
    /// exactly as <see cref="PausedStepViewModel.IsEnabled"/> is. A <see cref="RelayCommandAttribute"/>
    /// <c>CanExecute</c> predicate, not a plain field, so the bound buttons disable the moment it flips.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowCommandInRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowRoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyAlwaysCommand))]
    private bool isEnabled = true;

    public PendingPermissionViewModel(PendingPermission pending, AnswerPermissionDelegate answer)
    {
        ArgumentNullException.ThrowIfNull(pending);
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));

        PermissionRequestId = pending.PermissionRequestId;

        if (ShellCommandPatternMatcher.TryReadCommandLine(pending.ToolName, pending.ToolInputJson, out var commandLine)
            && !string.IsNullOrWhiteSpace(commandLine))
        {
            CommandFamily = ShellCommandPatternMatcher.ExtractCommandFamily(commandLine);
            PromptText = $"{pending.VendorTag} wants to run: {commandLine}";
        }
        else
        {
            CommandFamily = null;
            PromptText = $"{pending.VendorTag} wants to use {pending.ToolName}";
        }
    }

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task AllowOnceAsync() => _answer(PermissionRequestId, PermissionDecisionKind.AllowOnce, null);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task AllowCommandInRoomAsync() => _answer(PermissionRequestId, PermissionDecisionKind.AllowCommandInRoom, null);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task AllowRoomAsync() => _answer(PermissionRequestId, PermissionDecisionKind.AllowRoom, null);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task DenyAsync() => _answer(PermissionRequestId, PermissionDecisionKind.Deny, null);

    [RelayCommand(CanExecute = nameof(IsEnabled))]
    private Task DenyAlwaysAsync() => _answer(PermissionRequestId, PermissionDecisionKind.DenyAlways, null);
}
