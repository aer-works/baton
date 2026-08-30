using System.Text.Json;

namespace Baton.Domain;

/// <summary>
/// A versioned entry in a room memory document's version history (#672 M26 floor).
/// </summary>
public sealed record RoomMemoryVersion(
    int Version,
    string Operation,
    string TargetPath,
    string? Content,
    string Rationale,
    string Proposer,
    string Approver,
    DateTimeOffset Timestamp);

/// <summary>
/// A versioned room memory document owned by the room directory (#672 M26 floor, decision 0044).
/// Lifetime is coupled to the room directory, never to any conversation or session.
/// </summary>
public sealed record RoomMemoryDocument(
    int Version,
    string IndexContent,
    IReadOnlyDictionary<string, string> FactFiles,
    IReadOnlyList<RoomMemoryVersion> History)
{
    /// <summary>
    /// The document's on-disk layout under the room directory. Domain owns these names —
    /// <c>Mutation</c>'s applier imports them from here, never the reverse (this file is the one
    /// place the layout is stated; #672 review made it Domain's so the dependency arrow stays
    /// one-directional).
    /// </summary>
    public const string MemoryDirectoryName = "memory";

    /// <summary>
    /// Mechanically regenerated on every apply (never hand-edited): one line per fact file
    /// currently under <c>memory/</c>, sorted, so the orchestrator's turn-start read (0044 point 2)
    /// is always in sync with what is actually on disk rather than a record that can drift from a
    /// hand-maintained one.
    /// </summary>
    public const string IndexFileName = "INDEX.md";
    public const string VersionsFileName = "VERSIONS.jsonl";

    /// <summary>
    /// Loads the current room memory document and version from <paramref name="roomDirectoryPath"/>.
    /// </summary>
    /// <remarks>
    /// <b><see cref="FactFiles"/> and <see cref="History"/> are two independent reads and can
    /// legitimately disagree</b> — this method deliberately does NOT cross-check them. Two causes,
    /// only one of them a fault: decision 0044 permits the operator's own editor to touch
    /// <c>memory/</c> directly (a hand-added or hand-deleted fact file never gains a history
    /// entry), and a crash between the applier's fact-file write and its version append leaves an
    /// applied fact whose version record is missing (the applier's own remarks document that
    /// window). A checker here could not tell those apart, so a divergence surfaces to the caller
    /// as exactly what it is — both halves reported faithfully — rather than as a guess about
    /// which one is true.
    /// </remarks>
    public static async Task<RoomMemoryDocument> LoadAsync(
        string roomDirectoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);

        var memoryRoot = Path.Combine(roomDirectoryPath, MemoryDirectoryName);
        if (!Directory.Exists(memoryRoot))
        {
            return new RoomMemoryDocument(0, string.Empty, new Dictionary<string, string>(), Array.Empty<RoomMemoryVersion>());
        }

        var indexPath = Path.Combine(memoryRoot, IndexFileName);
        var indexContent = File.Exists(indexPath)
            ? await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        var versionsPath = Path.Combine(memoryRoot, VersionsFileName);
        var history = new List<RoomMemoryVersion>();
        if (File.Exists(versionsPath))
        {
            var lines = await File.ReadAllLinesAsync(versionsPath, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var versionRecord = JsonSerializer.Deserialize<RoomMemoryVersion>(line);
                    if (versionRecord is not null)
                    {
                        history.Add(versionRecord);
                    }
                }
                catch (JsonException ex)
                {
                    // Loud skip, never silent (error-handling rules): a corrupt history line loses
                    // one attribution record, not the document — but somebody has to hear about it.
                    Console.Error.WriteLine(
                        $"[RoomMemory] Skipping malformed version-history line in '{versionsPath}': {ex.Message}");
                }
            }
        }

        var currentVersion = history.Count > 0 ? history[^1].Version : 0;

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        var factFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(memoryRoot))
        {
            foreach (var file in Directory.GetFiles(memoryRoot, "*", enumeration))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(VersionsFileName, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(memoryRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                factFiles[relativePath] = content;
            }
        }

        return new RoomMemoryDocument(currentVersion, indexContent, factFiles, history.AsReadOnly());
    }
}
