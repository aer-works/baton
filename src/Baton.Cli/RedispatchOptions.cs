namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <see cref="RedispatchCommand"/> (#1441, spec/baton.md §2). Each field's own
/// doc below states only its parse-level shape; the command type states why.
/// </summary>
/// <param name="ParentRoomDirectoryPath">The room <see cref="RedispatchCommand"/> rebinds from.</param>
/// <param name="RoomDirectoryPath">Generated fresh by <see cref="RedispatchOptionsParser"/>; never an operator-supplied flag.</param>
/// <param name="SpecFilePath">Null keeps <see cref="ParentRoomDirectoryPath"/>'s built prompt as-is.</param>
/// <param name="Adapter">Null keeps the parent's.</param>
/// <param name="Model">Null keeps the parent's, subject to <see cref="RedispatchCommand.InheritBinding"/>'s own axis rule.</param>
/// <param name="Effort">Same as <see cref="Model"/>.</param>
/// <param name="WorkspaceDirectory">Null keeps the parent's.</param>
/// <param name="OutputPath">Parsed like <c>baton dispatch</c>'s own <c>--output</c>; never defaulted from the parent.</param>
/// <param name="Timeout">Null keeps the parent's.</param>
/// <param name="Label">The <c>--label</c> override (#1499) — see spec/baton.md §2 for the inheritance/clear/override contract.</param>
/// <param name="LabelSpecified">True when <c>--label</c> was explicitly provided, even if blank.</param>
/// <param name="TokenBudget">The <c>--token-budget</c> override (#1623). Null keeps the parent's.</param>
public sealed record RedispatchOptions(
    string ParentRoomDirectoryPath,
    string RoomDirectoryPath,
    string? SpecFilePath = null,
    string? Adapter = null,
    string? Model = null,
    string? Effort = null,
    string? WorkspaceDirectory = null,
    string? OutputPath = null,
    TimeSpan? Timeout = null,
    string? Label = null,
    bool LabelSpecified = false,
    long? TokenBudget = null);
