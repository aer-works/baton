namespace Aer.Ui.Core;

/// <summary>
/// The shell's destinations — docs/archive/ux/information-architecture.md's flat navigation: Home,
/// Author (M19 Phase 2, #187), Remote (M21 Phase 3, #234), Chat (M24 Phase 1, #262), plus Rooms
/// (M24 Phase 5, #278) — the fleet management view, distinct from Home's capped recents cards.
/// <para>
/// <c>Task</c> was removed by #1222: a room has one rendering, and <see cref="Chat"/> is it. The
/// member outlived its purpose in stages — #336 stopped it being a place a user navigated to,
/// #1196 slice 3 stopped rooms rendering in it, and what was left was a full-width graph reachable
/// only by Save &amp; Run and by typing a workflow *file*'s path into a box labelled "Room
/// directory".
/// </para>
/// </summary>
public enum ShellSection
{
    Home,
    Author,
    // #1068: renamed from Remote; its view now lives inside Views/SettingsView.
    Settings,
    Chat,
    Rooms,
}
