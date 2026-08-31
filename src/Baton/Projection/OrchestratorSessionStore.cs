using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Baton.Status;
using Baton.Store;

namespace Baton.Projection;

/// <summary>
/// Persistence store for engine session metadata under <c>{room}/.baton/orchestrator-session.json</c>.
/// Follows the same loud-fallback posture as <see cref="ProjectionCheckpointStore"/>:
/// missing or corrupt file → loud stderr message + cold start from zero.
/// </summary>
public static class OrchestratorSessionStore
{
    private const string BatonDirectoryName = ".baton";
    private const string CursorFileName = "orchestrator-session.json";

    public static string GetCursorFilePath(string roomDirectoryPath)
        => Path.Combine(roomDirectoryPath, BatonDirectoryName, CursorFileName);

    /// <summary>
    /// Computes the content-identity SHA-256 hex hash for a serialized event line (#972).
    /// </summary>
    public static string ComputeLineHash(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var bytes = Encoding.UTF8.GetBytes(line.TrimEnd('\r'));
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Reads raw lines from <c>room.jsonl</c> within <paramref name="roomDirectoryPath"/> using shared read permissions.
    /// </summary>
    public static string[] ReadRoomLogLines(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return [];
        }

        var roomLogPath = Path.Combine(roomDirectoryPath, BatonPaths.RoomLogFileName);
        if (!File.Exists(roomLogPath))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(roomLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var lastNewline = text.LastIndexOf('\n');
            var completeText = lastNewline >= 0 ? text[..(lastNewline + 1)] : string.Empty;
            return completeText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Loads the <see cref="OrchestratorSessionCursor"/> from <paramref name="roomDirectoryPath"/> if present and valid.
    /// If missing, corrupt, or invalid (including line hash mismatch or missing identity), logs LOUDLY to <see cref="Console.Error"/> and returns <c>null</c> to trigger a cold start.
    /// </summary>
    public static OrchestratorSessionCursor? Load(string roomDirectoryPath)
    {
        if (string.IsNullOrEmpty(roomDirectoryPath))
        {
            return null;
        }

        var filePath = GetCursorFilePath(roomDirectoryPath);
        if (!File.Exists(filePath))
        {
            // Silent by design, matching ProjectionCheckpointStore: a missing cursor is the
            // NORMAL state of every room that has never hosted a turn, not a fault. Only a file
            // that exists and cannot be honored is loud.
            return null;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            var cursor = JsonSerializer.Deserialize<OrchestratorSessionCursor>(json, FlowEventLogJson.Options);
            if (cursor is null || cursor.ProcessedEventCount < 0)
            {
                Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Cursor file '{filePath}' deserialized to null or negative count.");
                return null;
            }

            if (cursor.ProcessedEventCount == 0)
            {
                return cursor;
            }

            // Positive ProcessedEventCount requires a valid LastEventLineHash matching the line at index (ProcessedEventCount - 1)
            if (string.IsNullOrEmpty(cursor.LastEventLineHash))
            {
                Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Cursor in '{filePath}' has count {cursor.ProcessedEventCount} but carries no content identity hash (unverifiable legacy format or missing hash).");
                return null;
            }

            var lines = ReadRoomLogLines(roomDirectoryPath);
            if (lines.Length < cursor.ProcessedEventCount)
            {
                Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Cursor processed count ({cursor.ProcessedEventCount}) exceeds journal length ({lines.Length}).");
                return null;
            }

            var targetLineIndex = cursor.ProcessedEventCount - 1;
            var actualHash = ComputeLineHash(lines[targetLineIndex]);
            if (!string.Equals(actualHash, cursor.LastEventLineHash, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Content identity hash mismatch for event at index {targetLineIndex}. Expected '{cursor.LastEventLineHash}', actual '{actualHash}'. Journal was rewritten under cursor.");
                return null;
            }

            return cursor;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[OrchestratorSession] Cold start LOUDLY: Failed to load cursor from '{filePath}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Persists <paramref name="cursor"/> atomically to <c>.baton/orchestrator-session.json</c> within <paramref name="roomDirectoryPath"/>.
    /// </summary>
    public static void Save(string roomDirectoryPath, OrchestratorSessionCursor cursor)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentNullException.ThrowIfNull(cursor);

        var batonDir = Path.Combine(roomDirectoryPath, BatonDirectoryName);
        Directory.CreateDirectory(batonDir);

        var filePath = GetCursorFilePath(roomDirectoryPath);
        var json = JsonSerializer.Serialize(cursor, FlowEventLogJson.Options);

        var tempFilePath = filePath + ".tmp." + Guid.NewGuid().ToString("n");
        File.WriteAllText(tempFilePath, json);
        RetryingFileMove.Move(tempFilePath, filePath, overwrite: true);
    }
}
