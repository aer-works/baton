using Aer.RoomSession;

namespace Aer.Ui.Core;

/// <summary>
/// One row of the desktop Files section (#1340): a file's name, its summary text (version count,
/// latest author and time — <see cref="PlainLanguage.ForFileVersion"/>'s vocabulary), and its
/// version chain as chips reusing <see cref="ArtifactFileViewModel"/> — the same previewable-chip
/// shape the step drill-in's Outputs tab already uses, not a second one invented for this section.
/// </summary>
public sealed class RoomFileViewModel(string name, string summaryText, IReadOnlyList<ArtifactFileViewModel> versions)
{
    public string Name { get; } = name;
    public string SummaryText { get; } = summaryText;
    public IReadOnlyList<ArtifactFileViewModel> Versions { get; } = versions;
}

/// <summary>
/// Builds the desktop Files section's rows from <see cref="RoomFiles"/> (#1340) — a plain re-slice
/// into something a view can bind and click, the same "read model stays the one durable fact, this
/// just turns it into chips" shape <see cref="StepItemProjector"/> already uses for the step
/// drill-in's own file chips.
/// </summary>
public static class RoomFilesSectionProjector
{
    public static IReadOnlyList<RoomFileViewModel> Build(RoomFiles files, Func<string, Task> previewFileAsync)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(previewFileAsync);

        var rows = new List<RoomFileViewModel>(files.Files.Count);
        foreach (var file in files.Files)
        {
            var versionChips = new List<ArtifactFileViewModel>(file.Versions.Count);
            foreach (var version in file.Versions)
            {
                versionChips.Add(new ArtifactFileViewModel(
                    PlainLanguage.ForFileVersion(version.Worker, version.ProducedAt),
                    version.FilePath,
                    previewFileAsync,
                    select: selected => SelectVersion(versionChips, selected)));
            }

            var latest = file.Versions[^1];
            var versionCountText = file.Versions.Count == 1 ? "1 version" : $"{file.Versions.Count} versions";
            var summaryText = $"{versionCountText} · latest {PlainLanguage.ForFileVersion(latest.Worker, latest.ProducedAt)}";

            rows.Add(new RoomFileViewModel(file.Name, summaryText, versionChips));
        }

        return rows;
    }

    /// <summary>Mirrors <c>StepItemProjector.SelectOutputFile</c>'s sibling-clearing selection pattern, scoped to one file's version list.</summary>
    private static void SelectVersion(IReadOnlyList<ArtifactFileViewModel> versions, ArtifactFileViewModel selected)
    {
        foreach (var version in versions)
        {
            version.IsSelected = ReferenceEquals(version, selected);
        }
    }
}
