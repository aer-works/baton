using System.Text.Json;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Persistence store for room projection checkpoints (#903 Scope 1).
/// Checkpoints are saved under <c>.baton/checkpoint.json</c> inside the room directory.
/// </summary>
public static class ProjectionCheckpointStore
{
    private const string BatonDirectoryName = ".baton";
    private const string CheckpointFileName = "checkpoint.json";

    public static string GetCheckpointFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, BatonDirectoryName, CheckpointFileName);

    /// <summary>
    /// Loads the projection checkpoint from <paramref name="roomDirectoryPath"/> if present and valid.
    /// If the file is missing, corrupt, or unparseable, logs LOUDLY to <see cref="Console.Error"/>
    /// and returns <c>null</c> to trigger full event log replay.
    /// </summary>
    public static ProjectionCheckpoint? Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return null;
        }

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var checkpoint = JsonSerializer.Deserialize<ProjectionCheckpoint>(json, FlowEventLogJson.Options);
            if (checkpoint is null || checkpoint.Version < 2 || checkpoint.EventOffset < 0 || checkpoint.ByteOffset < 0 ||
                checkpoint.State is null || checkpoint.State.SucceededExecutionIds is null || checkpoint.State.AcceptedRequestByExecutionId is null ||
                checkpoint.State.CoreStartedExecutionIds is null || checkpoint.State.CoreExitedByExecutionId is null)
            {
                Console.Error.WriteLine($"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Checkpoint file '{filePath}' is missing version/aggregates or has negative offset.");
                return null;
            }

            return checkpoint;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectionCheckpoint] Fallback to full replay LOUDLY: Failed to load checkpoint from '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persists <paramref name="checkpoint"/> to <c>.baton/checkpoint.json</c> within <paramref name="roomDirectoryPath"/>.
    /// Assumes the caller holds the room directory's concurrency guard.
    /// </summary>
    public static void Save(string roomDirectoryPath, ProjectionCheckpoint checkpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(checkpoint);

        var batonDir = Path.Combine(roomDirectoryPath, BatonDirectoryName);
        Directory.CreateDirectory(batonDir);

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        var json = JsonSerializer.Serialize(checkpoint, FlowEventLogJson.Options);

        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }

    /// <summary>
    /// Deletes the checkpoint file if present. Used for determinism testing and forced full replays.
    /// </summary>
    public static void Delete(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return;
        }

        var filePath = GetCheckpointFilePath(roomDirectoryPath);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
