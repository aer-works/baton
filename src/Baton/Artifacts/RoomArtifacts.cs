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
/// #496 (decision 0021, trimmed to engine scope by the 2026-09-01 triage — no diff-and-choose UI,
/// spec/baton.md §1 forecloses that surface): the versioned, attributed model for a named artifact living
/// directly under a room's <c>artifacts/</c> — never for a file inside an execution's own
/// <c>artifacts/execution_&lt;id&gt;/</c> scratch directory, which decision 0021 point 2 already
/// calls plumbing that is never surfaced anywhere and this primitive does not touch.
/// <para>
/// <b>Layout.</b> <c>artifacts/&lt;name&gt;</c> stays the CURRENT version, so every existing reader
/// keeps working unchanged. Versions live beside it under <c>artifacts/.versions/&lt;name&gt;/&lt;n&gt;</c>
/// (the version's own bytes) with a sidecar <c>artifacts/.versions/&lt;name&gt;/index.jsonl</c> — one
/// <see cref="ArtifactVersionEntry"/> line per version. A dot-directory rather than a sibling: #1351's
/// filter convention already exists to keep engine mechanism out of every directory listing that
/// presents a room's file list, and a version history is exactly that kind of mechanism, not a
/// document a worker or harness should see enumerated. JSONL rather than one metadata file per
/// version: append-only, the same crash-safety shape <c>flow.jsonl</c> already relies on.
/// </para>
/// <para>
/// <b>The index is authoritative, the version file is not.</b> <see cref="Write"/> writes the version
/// file, then appends the index line, then replaces <c>artifacts/&lt;name&gt;</c> — in that order. A
/// crash between the first two steps leaves an orphan version file the index never names; every
/// reader here (<see cref="Versions"/>, <see cref="Read"/>) treats a version absent from the index as
/// never having happened, even though a stray file sits on disk, and the next <see cref="Write"/>
/// simply reuses that version number and overwrites the orphan.
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
    /// Absent target → version 1. Present and byte-identical → nothing new (<see cref="ArtifactWriteOutcome.Unchanged"/>).
    /// Present and different → version <c>n+1</c>, and <c>artifacts/&lt;name&gt;</c> is atomically
    /// replaced (temp file + move) so no reader ever observes a partial write.
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
    /// #496 point 4: prunes every named artifact's version HISTORY under <paramref name="artifactsRootPath"/>
    /// down to just its current version — the older version files and index lines behind it, never the
    /// content itself, since <c>artifacts/&lt;name&gt;</c> already holds the current version's bytes in
    /// full. <see cref="ArtifactPruner"/> calls this under the same terminal+not-kept+lock gate it
    /// already applies to <c>execution_*</c> directories: a room's version history is exactly as
    /// unbounded over a long room's lifetime as its execution directories are (issue #496's own closing
    /// line), so it sits inside the same retention boundary.
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
