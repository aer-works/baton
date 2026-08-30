using System.Text.Json;
using Baton.Flow.Domain;
using Baton.Flow.Store;

namespace Baton.Flow.Mutation;

/// <summary>
/// Applies one captured <see cref="MemoryProposalCapture"/> to a room's <c>memory/</c> directory
/// (decision 0044 point 3, #672 item 2). Called ONLY on operator approval — see
/// <see cref="MemoryProposalResolution"/>, the sole caller. Mirrors
/// <c>Baton.Mcp.Host.MemoryProposalTool</c>'s capture shape as a duplicated record for the same
/// cross-project-boundary reason <see cref="MemoryProposalEscalation.CaptureDirectoryName"/>
/// documents: <c>Baton.Flow</c> cannot reference <c>Baton.Mcp.Host</c>. Both
/// <c>MemoryProposalApplierTests</c> (this project) and <c>MemoryProposalToolTests</c>
/// (<c>Baton.Mcp.Host</c>'s own) exercise the identical JSON shape so the two sides cannot drift
/// unnoticed.
/// </summary>
public static class MemoryProposalApplier
{
    // The on-disk layout names live on RoomMemoryDocument (Domain owns the document's shape;
    // Mutation imports Domain, never the reverse). Referenced from there, not restated.

    /// <summary>
    /// How two filesystem paths are compared for equality here. Windows paths are case-insensitive
    /// and Linux/macOS paths are not, and getting this wrong in either direction is a defect: too
    /// strict refuses a legitimate path whose case differs, too loose accepts one that is genuinely
    /// a different file. One definition so the containment check and the resolution loop cannot
    /// disagree about what "the same path" means.
    /// </summary>
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Reads <paramref name="captureFilePath"/> and applies its proposed operation to
    /// <c>{roomDirectoryPath}/memory/</c>, then regenerates <see cref="RoomMemoryDocument.IndexFileName"/>.
    /// <paramref name="captureFilePath"/> must resolve strictly inside <c>memory/</c> after joining
    /// with the memory root — a traversal attempt (a rooted path, or a <c>../</c> segment that
    /// escapes the root) is refused loudly via <see cref="InvalidRoomMutationException"/>, never
    /// silently clamped or ignored. Deleting a target that does not exist is likewise a loud
    /// failure, not a silent success, per #672's explicit requirement.
    /// </summary>
    public static Task ApplyAsync(
        string roomDirectoryPath, string captureFilePath, CancellationToken cancellationToken)
        => ApplyAsync(roomDirectoryPath, captureFilePath, "unknown", "operator", cancellationToken);

    public static async Task ApplyAsync(
        string roomDirectoryPath,
        string captureFilePath,
        string proposer = "unknown",
        string approver = "operator",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(captureFilePath);

        if (!File.Exists(captureFilePath))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal capture file '{captureFilePath}' was not found; cannot apply.");
        }

        var json = await File.ReadAllTextAsync(captureFilePath, cancellationToken).ConfigureAwait(false);
        MemoryProposalCapture capture;
        try
        {
            capture = JsonSerializer.Deserialize<MemoryProposalCapture>(json)
                ?? throw new InvalidRoomMutationException(
                    $"Memory-proposal capture file '{captureFilePath}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal capture file '{captureFilePath}' is not valid JSON: {ex.Message}", ex);
        }

        var memoryRoot = Path.GetFullPath(Path.Combine(roomDirectoryPath, RoomMemoryDocument.MemoryDirectoryName));
        var resolvedTargetPath = ResolveTargetPathStrictlyInsideMemory(memoryRoot, capture.TargetPath);

        switch (capture.Operation)
        {
            case "add" or "edit":
                if (capture.Content is null)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal capture file '{captureFilePath}' has operation '{capture.Operation}' " +
                        "but no content.");
                }

                // 0044 point 3: nothing writes memory but an approved decision -- an 'add' that
                // silently overwrote an existing fact, or an 'edit' that silently created a new
                // one, would each contain a write nobody actually approved (the operator approved
                // the proposal they read, not whatever collided with it by the time this ran).
                // Loud refusal, same posture as the delete-of-a-missing-target guard below.
                var targetExists = File.Exists(resolvedTargetPath);
                if (capture.Operation == "add" && targetExists)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal 'add' target '{capture.TargetPath}' already exists under " +
                        $"'{memoryRoot}'; refusing to silently overwrite it (use 'edit' instead).");
                }

                if (capture.Operation == "edit" && !targetExists)
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal 'edit' target '{capture.TargetPath}' does not exist under " +
                        $"'{memoryRoot}'; refusing to silently create it (use 'add' instead).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(resolvedTargetPath)!);

                // Temp-then-move, matching MemoryProposalTool's own convention: a reader of memory/
                // never observes a partial write.
                var tempTargetPath = $"{resolvedTargetPath}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(tempTargetPath, capture.Content, cancellationToken)
                    .ConfigureAwait(false);
                RetryingFileMove.Move(tempTargetPath, resolvedTargetPath, overwrite: true, deleteSourceOnFinalFailure: true);
                break;

            case "delete":
                if (!File.Exists(resolvedTargetPath))
                {
                    throw new InvalidRoomMutationException(
                        $"Memory-proposal delete target '{capture.TargetPath}' does not exist under " +
                        $"'{memoryRoot}'; refusing to report a silent success.");
                }

                File.Delete(resolvedTargetPath);
                break;

            default:
                throw new InvalidRoomMutationException(
                    $"Memory-proposal capture file '{captureFilePath}' has unknown operation " +
                    $"'{capture.Operation}'.");
        }

        // The inner crash window, named like the outer apply-vs-resolve one in
        // MemoryProposalResolution's remarks: the fact write above and this version append are not
        // one transaction. A crash between them leaves an applied fact with no version record --
        // RoomMemoryDocument.LoadAsync then reports the fact file with an undercounting Version,
        // and its remarks say why it must NOT auto-detect that (0044 permits operator hand-edits,
        // which look identical). Recovery is the same as the outer window's: the item is still
        // pending, a retried `add` fails loudly on the existing target, and reject resolves it
        // with memory/ already reflecting the landed write. Proven observable by
        // RoomMemoryDocumentTests.A_crash_between_fact_write_and_version_append_is_visible_as_fact_history_divergence.
        await RecordVersionAsync(memoryRoot, capture, proposer, approver, cancellationToken).ConfigureAwait(false);
        RegenerateIndex(memoryRoot);
    }

    /// <summary>
    /// Joins <paramref name="targetPath"/> onto <paramref name="memoryRoot"/> and canonicalizes,
    /// then requires the result to sit strictly inside the root. <see cref="Path.Combine"/> returns
    /// a rooted second argument verbatim (ignoring the first), so a rooted <paramref
    /// name="targetPath"/> (an absolute Windows or Unix path) surfaces here as a canonical path
    /// outside <paramref name="memoryRoot"/> exactly like a <c>../</c> escape does — one guard
    /// catches both shapes, non-negotiable per #672.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> is purely lexical: it collapses <c>..</c> segments
    /// textually but never asks the filesystem whether a directory along the way is a junction or
    /// symlink. #856: a reparse point already sitting under <paramref name="memoryRoot"/> (a
    /// junction is creatable by anything with plain write access to the room directory, no admin
    /// needed on Windows -- see <see cref="ResolveReparsePointsIgnoringMissingTail"/>) passes the
    /// lexical check above and would let the actual disk write land wherever the link points,
    /// because the OS follows reparse points transparently for every normal file API. This is
    /// defense-in-depth for the engine's own promise that an approved apply writes strictly inside
    /// <c>memory/</c> -- not a privilege boundary: an attacker who can already place a junction
    /// under <c>memory/</c> already has write access to the room directory and could edit
    /// <c>memory/</c> directly.
    /// </remarks>
    internal static string ResolveTargetPathStrictlyInsideMemory(string memoryRoot, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new InvalidRoomMutationException("Memory-proposal targetPath must not be empty.");
        }

        var combined = Path.GetFullPath(Path.Combine(memoryRoot, targetPath));

        var rootWithSeparator = memoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? memoryRoot
            : memoryRoot + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal targetPath '{targetPath}' resolves outside memory/ (to '{combined}'); refused.");
        }

        // The lexical check above passed. Re-check containment against the reparse-resolved path:
        // a junction/symlink for an ancestor directory (or the leaf itself) that exists today under
        // memoryRoot can redirect the write outside it even though the string-only check above is
        // satisfied. A link that resolves back inside memoryRoot is left alone (item 2, #856) --
        // this only refuses the case where resolution actually escapes.
        //
        // Both sides of this comparison MUST go through the identical resolution walk. An earlier
        // version of this fix resolved memoryRoot only if memoryRoot itself was a reparse point,
        // while resolving combined by walking every segment beneath it -- so if the room directory
        // is itself reached through a junction (memoryRoot is an ordinary directory, but one of
        // its own ancestors is a link), realCombined came back fully resolved past that ancestor
        // while realRoot stayed lexical, and a legitimate in-tree alias was wrongly refused. Proven
        // by reproduction: rooting the temp room directory itself behind a junction and re-running
        // the allow-arm reproduced exactly this false positive before this fix.
        var realRoot = ResolveReparsePointsIgnoringMissingTail(memoryRoot);
        var realCombined = ResolveReparsePointsIgnoringMissingTail(combined);

        var caseComparison = PathComparison;

        var realRootWithSeparator = realRoot.EndsWith(Path.DirectorySeparatorChar)
            ? realRoot
            : realRoot + Path.DirectorySeparatorChar;

        if (!string.Equals(realCombined, realRoot, caseComparison)
            && !realCombined.StartsWith(realRootWithSeparator, caseComparison))
        {
            throw new InvalidRoomMutationException(
                $"Memory-proposal targetPath '{targetPath}' resolves outside memory/ through a reparse point " +
                $"(to '{realCombined}'); refused.");
        }

        return combined;
    }

    /// <summary>
    /// Fully resolves <paramref name="path"/> by walking every segment from its filesystem root
    /// down, resolving each existing ancestor that is itself a reparse point (following chained
    /// links via <c>returnFinalTarget: true</c>) before appending the next segment. A segment that
    /// does not exist yet (the common case for an 'add' whose parent directories get created later
    /// by <see cref="ApplyAsync"/>) is appended literally with no resolution attempted -- there is
    /// nothing on disk yet for it to redirect through. Starting from the root rather than from
    /// <c>memoryRoot</c> is what lets <c>memoryRoot</c> itself and a target beneath it be resolved
    /// symmetrically, even when an ancestor of <c>memoryRoot</c> (not memoryRoot itself) is the
    /// reparse point.
    /// </summary>
    /// <remarks>
    /// Walked to a FIXED POINT rather than once, because one walk is only correct if
    /// <c>ResolveLinkTarget</c>'s own result is already fully normalised -- and that is
    /// platform-specific, which the first version of this guard did not account for. Measured both
    /// ways: on Windows the returned target has every ancestor resolved, while on Linux it comes
    /// back as stored. So a link whose target is expressed *through another link* left an unresolved
    /// ancestor in the result, and comparing that against a fully-resolved <c>memoryRoot</c> refused
    /// a legitimate in-tree alias. That is a false refusal, not a missed escape -- but it broke on
    /// exactly the platform the original measurement never covered, and CI on this PR is what caught
    /// it. Re-walking until nothing changes is correct on both, and costs one extra no-op pass where
    /// the OS has already done the work.
    /// </remarks>
    private static string ResolveReparsePointsIgnoringMissingTail(string path)
    {
        // Each pass that changes anything has resolved at least one reparse point, and both OSes cap
        // their own chain following far below this. Reaching the cap means a link arrangement that
        // will not settle, which is refused rather than looped on -- the same posture as #874.
        const int MaxPasses = 64;

        var current = path;
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = ResolveReparsePointsOnce(current);
            if (string.Equals(next, current, PathComparison))
            {
                return current;
            }

            current = next;
        }

        throw new InvalidRoomMutationException(
            $"Memory-proposal target path '{path}' was still changing after {MaxPasses} reparse-point resolution " +
            "passes; refused, because a path that will not settle cannot be shown to stay inside memory/.");
    }

    /// <summary>
    /// One resolution pass for <see cref="ResolveReparsePointsIgnoringMissingTail"/>, which owns the
    /// reason this is called repeatedly.
    /// </summary>
    private static string ResolveReparsePointsOnce(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return ResolveIfReparsePoint(root);
        }

        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

        var current = root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                current = ResolveIfReparsePoint(current);
            }
        }

        return current;
    }

    /// <summary>
    /// Returns <paramref name="path"/> unchanged unless it exists and is itself a reparse point, in
    /// which case returns the fully-resolved final target (chained junctions/symlinks included).
    /// </summary>
    private static string ResolveIfReparsePoint(string path)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            return path;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return path;
        }

        try
        {
            var resolved = isDirectory
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);

            return resolved?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // #874: two measured ways this call fails on a reparse point that every guard above has
            // already accepted, and neither is screened out by the Directory.Exists check -- a
            // junction reports as an existing directory in both cases, and File.GetAttributes
            // succeeds in both.
            //   - a cycle (A -> B -> A): `returnFinalTarget: true` walks the whole chain, so
            //     resolution fails with IOException.
            //   - a link whose own ACL denies this process read access: UnauthorizedAccessException,
            //     which does NOT derive from IOException and so needs naming separately. Catching
            //     only IOException here is precisely the bug an earlier draft of this shipped.
            // #874 carries both runs. Refusing is the only honest answer -- a link whose target
            // cannot be determined cannot be shown to land inside memory/, and returning `path`
            // unresolved would silently downgrade to the lexical check this method exists to replace.
            throw new InvalidRoomMutationException(
                $"Memory-proposal target path component '{path}' is a reparse point whose target could not be " +
                $"resolved ({ex.Message}); refused, because an unresolvable link cannot be shown to stay inside memory/.",
                ex);
        }
    }

    /// <summary>
    /// #875: the enumeration skips reparse points rather than walking through them. The write side
    /// refuses a link that leaves memory/, but a plain recursive enumeration follows one
    /// transparently — so a junction that is present for any reason would have its outside contents
    /// listed in the index as though they were this room's own facts, and the index is what the
    /// orchestrator reads at every turn start.
    /// <para>
    /// The skip is by attribute, so it applies to EVERY reparse point, including one that resolves
    /// back inside memory/ and is therefore still perfectly writable (#856 item 2). State the
    /// consequence rather than let the word "skip" hide it: an in-tree alias's own NAME does not
    /// appear in the index. No fact's content is lost — the walk reaches the same bytes directly, at
    /// the real path — but the alias is not an addressable entry the orchestrator can see.
    /// </para>
    /// <para>
    /// That is chosen over resolving-and-filtering, which would list both names for one fact and,
    /// worse, cannot be made safe for a directory junction pointing at its own ancestor: following
    /// that recurses forever. The allow-polarity test in <c>MemoryProposalApplierTests</c> carries a
    /// pair of index assertions that pin this trade-off in both directions, so it cannot flip
    /// silently. (Named by class rather than by method on purpose: spelling the method out here
    /// reproduces its whole sentence, which is a restatement the record-once gate catches.)
    /// </para>
    /// <para>
    /// Every <see cref="EnumerationOptions"/> property that differs from the
    /// <see cref="SearchOption"/> overload this replaced was checked rather than assumed equivalent;
    /// #875 carries the property-by-property comparison and the run behind it.
    /// </para>
    /// </summary>
    private static void RegenerateIndex(string memoryRoot)
    {
        Directory.CreateDirectory(memoryRoot);

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,

            // The one differing property that is NOT inert: `new EnumerationOptions()` defaults this
            // to true, so taking the default would silently drop an unreadable fact file from the
            // index -- trading a visible failure for an index that quietly under-reports. Set
            // explicitly to match the overload being replaced (measured, in #875).
            IgnoreInaccessible = false,
        };

        var factFiles = Directory.GetFiles(memoryRoot, "*", enumeration)
            .Where(f => !Path.GetFileName(f).Equals(RoomMemoryDocument.IndexFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals(RoomMemoryDocument.VersionsFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(memoryRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string>
        {
            "# Memory index",
            "",
            "Mechanically regenerated on every applied memory proposal -- do not edit by hand.",
            "",
        };
        lines.AddRange(factFiles.Select(f => $"- {f}"));

        var indexPath = Path.Combine(memoryRoot, RoomMemoryDocument.IndexFileName);
        var tempIndexPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllLines(tempIndexPath, lines);
        RetryingFileMove.Move(tempIndexPath, indexPath, overwrite: true, deleteSourceOnFinalFailure: true);
    }

    private static async Task RecordVersionAsync(
        string memoryRoot,
        MemoryProposalCapture capture,
        string proposer,
        string approver,
        CancellationToken cancellationToken)
    {
        var versionsPath = Path.Combine(memoryRoot, RoomMemoryDocument.VersionsFileName);
        var currentMaxVersion = 0;
        var lines = new List<string>();

        if (File.Exists(versionsPath))
        {
            var existingLines = await File.ReadAllLinesAsync(versionsPath, cancellationToken).ConfigureAwait(false);
            foreach (var line in existingLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                lines.Add(line);
                try
                {
                    var versionRecord = JsonSerializer.Deserialize<Domain.RoomMemoryVersion>(line);
                    if (versionRecord is not null && versionRecord.Version > currentMaxVersion)
                    {
                        currentMaxVersion = versionRecord.Version;
                    }
                }
                catch (JsonException ex)
                {
                    // The line is preserved verbatim above (never dropped on rewrite), but it can't
                    // vote on the max version — loud skip, never silent (error-handling rules).
                    Console.Error.WriteLine(
                        $"[RoomMemory] Malformed version-history line in '{versionsPath}' ignored for version numbering: {ex.Message}");
                }
            }
        }

        var nextVersion = currentMaxVersion + 1;
        var record = new Domain.RoomMemoryVersion(
            nextVersion,
            capture.Operation,
            capture.TargetPath,
            capture.Content,
            capture.Rationale,
            proposer,
            approver,
            DateTimeOffset.UtcNow);

        lines.Add(JsonSerializer.Serialize(record));

        var tempVersionsPath = $"{versionsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllLinesAsync(tempVersionsPath, lines, cancellationToken).ConfigureAwait(false);
        RetryingFileMove.Move(tempVersionsPath, versionsPath, overwrite: true, deleteSourceOnFinalFailure: true);
    }
}

/// <summary>The structured shape a capture file holds, mirroring <c>Baton.Mcp.Host.MemoryProposalTool</c>'s own record of the same name.</summary>
public sealed record MemoryProposalCapture(string Operation, string TargetPath, string? Content, string Rationale);
