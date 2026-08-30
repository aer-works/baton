namespace Baton.Flow.Artifacts;

/// <summary>
/// M24 / ADR 0009: Keep/durable marker file for rooms — a plain directory-native marker file
/// (<c>.baton/keep</c>): existence-checked, never a schema field.
/// A run marked with keep is exempt from artifact pruning (#973).
/// </summary>
public static class KeepMarker
{
    public const string KeepFileName = "keep";

    public static string MarkerFilePath(string roomDirectoryPath) =>
        Path.Combine(roomDirectoryPath, ".baton", KeepFileName);

    public static bool IsKept(string roomDirectoryPath) =>
        File.Exists(MarkerFilePath(roomDirectoryPath));

    public static async Task MarkKeepAsync(string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        var markerPath = MarkerFilePath(roomDirectoryPath);
        var dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    public static Task ClearKeepAsync(string roomDirectoryPath)
    {
        var markerPath = MarkerFilePath(roomDirectoryPath);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }

        return Task.CompletedTask;
    }
}
