using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Store;

namespace Baton.Artifacts;

/// <summary>Who produced a version, and with what. All fields are null when unknown — e.g. a
/// conductor-authored delivery ties to no <c>ExecutionId</c>, and a room's own frozen bindings can
/// predate an adapter/model being recorded.</summary>
public sealed record ArtifactAttribution(
    [property: JsonPropertyName("executionId")] string? ExecutionId,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("adapter")] string? Adapter,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>One line of <c>artifacts/.versions/&lt;name&gt;/index.jsonl</c> — the durable, sole
/// source of truth for which versions of a named artifact exist (<see cref="RoomArtifacts"/>'s own
/// remarks on <see cref="RoomArtifacts.Write"/> state why a version file on disk is not enough by
/// itself).</summary>
public sealed record ArtifactVersionEntry(
    [property: JsonPropertyName("n")] int Version,
    [property: JsonPropertyName("producedAt")] string ProducedAt,
    [property: JsonPropertyName("producedBy")] ArtifactAttribution ProducedBy,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("bytes")] long Bytes);

/// <summary>What <see cref="RoomArtifacts.Write"/> actually did, so a caller can say so (#496's
/// design explicitly calls for a caller-visible distinction between "wrote nothing new" and "versioned").</summary>
public enum ArtifactWriteOutcome
{
    /// <summary>The name did not exist; this write became version 1.</summary>
    Created,

    /// <summary>The name existed with different bytes; this write became version <c>n+1</c>.</summary>
    Versioned,

    /// <summary>The name existed with byte-identical content; nothing new was written.</summary>
    Unchanged,
}

public sealed record ArtifactWriteResult(ArtifactWriteOutcome Outcome, int Version, string CurrentPath);

/// <summary>
/// #496: the write/read primitive behind a named artifact's version history — one primitive for
/// anything sitting directly under a room's <c>artifacts/</c>, never for a file inside an
/// execution's own scratch <c>artifacts/execution_&lt;id&gt;/</c>. The design rationale (decision
/// 0021, why its scope stops here, why a dot-directory, why JSONL, and the exact population of
/// writers routed through this) is spec/baton.md §2's canonical record — this comment states only
/// what the code does.
/// <para>
/// <c>artifacts/&lt;name&gt;</c> holds the current bytes. Each older version sits at
/// <c>artifacts/.versions/&lt;name&gt;/&lt;n&gt;</c>, named by one <see cref="ArtifactVersionEntry"/>
/// line in the sidecar <c>artifacts/.versions/&lt;name&gt;/index.jsonl</c>.
/// </para>
/// <para>
/// Write order matters for recovery: the numbered file lands first, its index line second, the
/// current file last. Dying between the first two leaves a numbered file nothing names — both
/// <see cref="Versions"/> and <see cref="Read"/> consult the index alone, so an un-indexed file
/// simply is not a version, and the number is free for the next call to reuse.
/// </para>
/// </summary>
public static class RoomArtifacts
{
    public const string VersionsDirectoryName = ".versions";
    public const string IndexFileName = "index.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes <paramref name="content"/> under the named artifact <paramref name="name"/> (a path
    /// relative to <c>artifacts/</c>, which may include subdirectories — e.g. <c>"conductor/x.md"</c>).
    /// See <see cref="ArtifactWriteOutcome"/>'s three members for the exact branching this takes.
    /// The eventual replacement of <c>artifacts/&lt;name&gt;</c> goes through a temp file plus move, so
    /// no reader ever observes a half-written file.
    /// </summary>
    public static ArtifactWriteResult Write(
        string roomDirectoryPath,
        string name,
        byte[] content,
        ArtifactAttribution attribution,
        DateTimeOffset? producedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(attribution);

        var relativeName = NormalizeName(name);
        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var currentPath = Path.Combine(artifactsRoot, relativeName);
        var versionsDir = VersionsDirectory(artifactsRoot, relativeName);
        var indexPath = Path.Combine(versionsDir, IndexFileName);

        var newSha256 = Sha256Hex(content);
        var existingVersions = ReadIndex(indexPath);
        var latest = existingVersions.Count > 0 ? existingVersions[^1] : null;

        if (File.Exists(currentPath) && Sha256Hex(File.ReadAllBytes(currentPath)) == newSha256)
        {
            return new ArtifactWriteResult(ArtifactWriteOutcome.Unchanged, latest?.Version ?? 0, currentPath);
        }

        var nextVersion = (latest?.Version ?? 0) + 1;
        Directory.CreateDirectory(versionsDir);
        WriteAtomic(Path.Combine(versionsDir, VersionFileName(nextVersion)), content);

        var entry = new ArtifactVersionEntry(
            nextVersion,
            (producedAtUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            attribution,
            newSha256,
            content.LongLength);
        AppendIndexLine(indexPath, entry);

        Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
        WriteAtomic(currentPath, content);

        var outcome = nextVersion == 1 ? ArtifactWriteOutcome.Created : ArtifactWriteOutcome.Versioned;
        return new ArtifactWriteResult(outcome, nextVersion, currentPath);
    }

    /// <summary>The version history for <paramref name="name"/>, oldest first, from the index alone
    /// — empty when the name has never been written through <see cref="Write"/>.</summary>
    public static IReadOnlyList<ArtifactVersionEntry> Versions(string roomDirectoryPath, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);
        var indexPath = Path.Combine(VersionsDirectory(artifactsRoot, NormalizeName(name)), IndexFileName);
        return ReadIndex(indexPath);
    }

    /// <summary>
    /// Reads <paramref name="name"/>'s content. <paramref name="version"/> null reads the current
    /// file directly (the same path every pre-#496 reader already used). A specific version number
    /// not present in the index — including an orphan version file a crash left behind — reads as
    /// null, never as the stray bytes on disk.
    /// </summary>
    public static byte[]? Read(string roomDirectoryPath, string name, int? version = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var relativeName = NormalizeName(name);
        var artifactsRoot = Path.Combine(roomDirectoryPath, ArtifactManager.ArtifactsDirectoryName);

        if (version is null)
        {
            var currentPath = Path.Combine(artifactsRoot, relativeName);
            return File.Exists(currentPath) ? File.ReadAllBytes(currentPath) : null;
        }

        var versionsDir = VersionsDirectory(artifactsRoot, relativeName);
        var isIndexed = ReadIndex(Path.Combine(versionsDir, IndexFileName)).Any(e => e.Version == version.Value);
        if (!isIndexed)
        {
            return null;
        }

        var versionFilePath = Path.Combine(versionsDir, VersionFileName(version.Value));
        return File.Exists(versionFilePath) ? File.ReadAllBytes(versionFilePath) : null;
    }

    /// <summary>
    /// #496 point 4 (spec/baton.md §2): discards every superseded version file and index line under
    /// <paramref name="artifactsRootPath"/>, keeping only each name's newest entry. Never touches
    /// <c>artifacts/&lt;name&gt;</c> itself. <see cref="ArtifactPruner"/> is the sole caller, from
    /// inside the terminal+not-kept+lock gate it already established for <c>execution_*</c>.
    /// </summary>
    public static bool PruneVersionHistory(string artifactsRootPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRootPath);

        var versionsRoot = Path.Combine(artifactsRootPath, VersionsDirectoryName);
        if (!Directory.Exists(versionsRoot))
        {
            return false;
        }

        var prunedAny = false;
        foreach (var indexPath in Directory.GetFiles(versionsRoot, IndexFileName, SearchOption.AllDirectories))
        {
            var versionDir = Path.GetDirectoryName(indexPath)!;
            var entries = ReadIndex(indexPath);
            if (entries.Count <= 1)
            {
                continue;
            }

            var latest = entries[^1];
            foreach (var entry in entries)
            {
                if (entry.Version == latest.Version)
                {
                    continue;
                }

                var versionFilePath = Path.Combine(versionDir, VersionFileName(entry.Version));
                if (File.Exists(versionFilePath))
                {
                    File.Delete(versionFilePath);
                    prunedAny = true;
                }
            }

            WriteIndexAtomic(indexPath, [latest]);
        }

        return prunedAny;
    }

    private static string VersionsDirectory(string artifactsRoot, string relativeName) =>
        Path.Combine(artifactsRoot, VersionsDirectoryName, relativeName);

    private static string VersionFileName(int version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Refuses an absolute path, an empty segment, or a <c>.</c>/<c>..</c> traversal segment
    /// — an artifact name is always relative to <c>artifacts/</c>, the same posture
    /// <c>MemoryProposalApplier.ResolveTargetPathStrictlyInsideMemory</c> takes for <c>memory/</c>.</summary>
    private static string NormalizeName(string name)
    {
        var segments = name.Split('/', '\\');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
        {
            throw new ArgumentException(
                $"Artifact name '{name}' must be a relative path with no empty, '.', or '..' segments.", nameof(name));
        }

        return Path.Combine(segments);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static List<ArtifactVersionEntry> ReadIndex(string indexPath)
    {
        var entries = new List<ArtifactVersionEntry>();
        if (!File.Exists(indexPath))
        {
            return entries;
        }

        foreach (var line in File.ReadAllLines(indexPath, Utf8NoBom))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<ArtifactVersionEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A torn append (a crash mid-write of this single line) is the only way a line here
                // is unparsable -- the index is the source of truth for what happened, so a line that
                // cannot even be read as an entry carries no version and is skipped, never treated as
                // corrupting every version after it.
            }
        }

        entries.Sort((a, b) => a.Version.CompareTo(b.Version));
        return entries;
    }

    private static void AppendIndexLine(string indexPath, ArtifactVersionEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        File.AppendAllText(indexPath, JsonSerializer.Serialize(entry, JsonOptions) + "\n", Utf8NoBom);
    }

    private static void WriteIndexAtomic(string indexPath, IReadOnlyList<ArtifactVersionEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
        }

        WriteAtomic(indexPath, Utf8NoBom.GetBytes(sb.ToString()));
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, content);
        RetryingFileMove.Move(tempPath, path, overwrite: true, deleteSourceOnFinalFailure: true);
    }
}
