namespace Aer.Adapters;

/// <summary>
/// M24 Phase 5 (#278): whether a room is archived, keyed off a directory-native marker file — the
/// same idiom <see cref="InteractiveSessionMaterializer"/> uses for <c>.aer/workflow-path</c>/
/// <c>.aer/bindings-path</c> (plain file, existence-checked, never a schema field). The writer/eraser
/// half of this (the daemon's archive/unarchive routes) was deleted with the daemon's HTTP surface
/// (#1420, #1421) — this stays because <see cref="BuiltInWorkflowTemplates"/> and
/// <see cref="InteractiveSessionMaterializer"/> still read it to give a clearer collision message
/// (<see cref="RoomDirectoryAlreadyExistsException"/>) than a bare "already exists" for a room a
/// person meant to reuse.
/// </summary>
public static class RoomLifecycle
{
    private const string ArchivedMarkerFileName = "archived";

    private static string MarkerFilePath(string roomDirectoryPath) => Path.Combine(roomDirectoryPath, ".aer", ArchivedMarkerFileName);

    public static bool IsArchived(string roomDirectoryPath) => File.Exists(MarkerFilePath(roomDirectoryPath));
}
