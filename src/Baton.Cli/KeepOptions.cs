namespace Baton.Cli;

/// <summary>
/// Parsed arguments shared by <c>baton keep</c> and <c>baton unkeep</c> (#1156) — both take exactly
/// the room directory, nothing else, so one options shape serves both verbs' parsers.
/// </summary>
/// <param name="RoomDirectoryPath">
/// record-once-ok: #443 src/Baton.Cli/CancelOptions.cs
/// An already-started room's durable state directory — neither verb binds a fresh snapshot the way
/// <c>baton run</c> does ("mutation commands never bind fresh" rule).
/// </param>
public sealed record KeepOptions(string RoomDirectoryPath);
