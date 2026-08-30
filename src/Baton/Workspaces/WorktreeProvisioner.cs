using System.ComponentModel;
using System.Diagnostics;

namespace Baton.Workspaces;

/// <summary>
/// Provisions a git worktree as a worker's workspace and tears it down once the room is Terminal —
/// the engine half of #669, so a reviewer can be dispatched at a branch without a human checking it
/// out anywhere, and without the review and the ongoing work fighting over one tree.
///
/// <para>
/// Vendor-agnostic (Architecture Rule 2): <c>Baton</c> never learns which vendor runs in the tree —
/// git is infrastructure, not an AI vendor, so this belongs beside <c>ArtifactManager</c> in the
/// dispatch layer rather than in <c>Baton.Vendors</c>. <b>Local worktrees only</b> — no clone, no fetch,
/// no network: a worktree of a repository already on disk needs no credential, so Rule 4 (Credential
/// Isolation) is untouched. The moment this grows a clone it acquires a credential problem, which is a
/// different decision (#669).
/// </para>
/// </summary>
public static class WorktreeProvisioner
{
    /// <summary>
    /// The bind-time check, separated so a caller can refuse a bad spec before the pump starts rather
    /// than discovering it at dispatch (#668's class). The repository must be an absolute, fully
    /// qualified path — AER and the worker resolve a relative one against different bases, so the run
    /// would fail its contract after paying in full (#668; <see cref="Path.IsPathFullyQualified(string)"/>,
    /// not <c>IsPathRooted</c>, is the predicate that actually means it, since <c>IsPathRooted("C:x")</c>
    /// is true while the path is still relative to a drive's current directory) — and the ref must be
    /// non-empty.
    /// </summary>
    public static void ValidateSpec(string repository, string reference)
    {
        if (string.IsNullOrWhiteSpace(repository) || !Path.IsPathFullyQualified(repository))
        {
            throw new InvalidWorkspaceSpecException(
                $"A worktree workspace needs an absolute repository path; '{repository}' is not fully " +
                "qualified. A relative path resolves against a different base for AER and the worker, so " +
                "the run would fail its contract after paying in full (#668).");
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new InvalidWorkspaceSpecException(
                "A worktree workspace needs a non-empty git ref (a branch or commit) to check out.");
        }
    }

    /// <summary>
    /// Detects whether <paramref name="directoryPath"/> is a provisioned git worktree by checking
    /// if <c>git rev-parse --git-common-dir</c> differs from <c>--git-dir</c> (#1354). Returns false when
    /// the directory does not exist, git ran and reported the path is not a worktree (a non-git
    /// directory, or a main repository root), or git's output was unreadable.
    /// </summary>
    /// <exception cref="WorktreeProvisioningException">
    /// git itself could not be run (missing from PATH) — a distinct failure from "not a worktree"
    /// (finding 10, #1354/#1380): folding the two together previously reported a missing git the same
    /// way as an ordinary directory, so the caller went on to attempt a provision that would fail again
    /// with a different, less direct message.
    /// </exception>
    public static bool IsWorktree(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return false;
        }

        (int ExitCode, string StdOut, string StdErr) result;
        try
        {
            result = RunGit(directoryPath, "rev-parse", "--git-common-dir", "--git-dir");
        }
        catch (WorktreeProvisioningException ex)
        {
            throw new WorktreeProvisioningException(
                $"Could not determine whether '{directoryPath}' is a worktree: {ex.Message}");
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return false;
        }

        var lines = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return false;
        }

        var commonDir = Path.GetFullPath(Path.Combine(directoryPath, lines[0]));
        var gitDir = Path.GetFullPath(Path.Combine(directoryPath, lines[1]));

        return !string.Equals(
            NormalizeForComparison(commonDir),
            NormalizeForComparison(gitDir),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a git worktree of <paramref name="repository"/> at <paramref name="reference"/> at the
    /// absolute <paramref name="worktreePath"/> — the value the worker's WorkingDirectory then points
    /// at. The caller owns the path so a room with several workers gives each its own tree (one
    /// worktree per worker, never shared). Validates the spec first (<see cref="ValidateSpec"/>); a git
    /// failure (an unknown ref, a ref already checked out elsewhere) throws
    /// <see cref="WorktreeProvisioningException"/>.
    /// </summary>
    public static void Provision(string worktreePath, string repository, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ValidateSpec(repository, reference);

        var parent = Path.GetDirectoryName(worktreePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent); // git worktree add needs the leaf's parent to exist
        }

        var (exitCode, _, stderr) = RunGit(repository, "worktree", "add", worktreePath, reference);
        if (exitCode != 0)
        {
            // Serialized against concurrent provisioning by the room's ConcurrencyGuard; this check also
            // handles a leftover worktree from a prior crashed run whose teardown did not complete.
            if (IsRegisteredWorktreeForRef(repository, worktreePath, reference))
            {
                return;
            }

            throw new WorktreeProvisioningException(
                $"Provisioning a worktree of '{reference}' from '{repository}' failed (git worktree add, " +
                $"exit {exitCode}): {stderr.Trim()}");
        }
    }

    /// <summary>
    /// Removes the worktree at <paramref name="worktreePath"/> once the room is Terminal. <b>Never
    /// throws</b> — a teardown fault must not fail a room that has already completed. Two of the three
    /// outcomes are not a removal: a tree carrying <b>uncommitted changes is kept</b> (discarding a
    /// worker's only output is worse than leaving a directory behind), and a removal <b>blocked by a
    /// still-held file</b> — a live build process holding an output, observed repeatedly on this host —
    /// is reported rather than forced. A path that is already gone is reported as removed.
    /// </summary>
    public static WorktreeTeardownResult Teardown(string repository, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null);
        }

        try
        {
            // `git status --porcelain` prints one line per dirty path and nothing at all when clean.
            var (statusCode, statusOut, _) = RunGit(worktreePath, "status", "--porcelain");
            if (statusCode == 0 && !string.IsNullOrWhiteSpace(statusOut))
            {
                return new WorktreeTeardownResult(
                    WorktreeTeardownOutcome.KeptUncommitted, worktreePath,
                    "kept: the worktree carries uncommitted changes, and discarding a worker's only output " +
                    "is worse than leaving a directory behind");
            }

            var (removeCode, _, removeErr) = RunGit(repository, "worktree", "remove", worktreePath);
            return removeCode == 0
                ? new WorktreeTeardownResult(WorktreeTeardownOutcome.Removed, worktreePath, null)
                : new WorktreeTeardownResult(
                    WorktreeTeardownOutcome.RemovalBlocked, worktreePath,
                    $"removal did not complete (typically a live build process still holds a file under it): " +
                    removeErr.Trim());
        }
        catch (Exception ex) when (ex is WorktreeProvisioningException or IOException)
        {
            // The "never throws" half of the contract: a git that could not even run (missing from PATH,
            // or a transient IO fault reading its output) becomes a reported blocked removal, never an
            // exception out of a run that has already reached Terminal.
            return new WorktreeTeardownResult(
                WorktreeTeardownOutcome.RemovalBlocked, worktreePath, "removal could not run git: " + ex.Message);
        }
    }

    /// <summary>
    /// Audits a provisioned worktree after an execution exit-0 natural completion (#901).
    /// Runs <c>git status --porcelain</c> inside <paramref name="worktreePath"/>.
    /// Returns clean if no uncommitted/stray paths exist; otherwise returns dirty with a diagnostic
    /// reason naming up to 10 stray paths and total count. A git error fails closed.
    /// </summary>
    public static WorktreeAuditResult Audit(string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return new WorktreeAuditResult(
                IsClean: false,
                FailureReason: $"Grant audit failed: worktree directory '{worktreePath}' does not exist or is missing.");
        }

        try
        {
            var (exitCode, stdout, stderr) = RunGit(worktreePath, "status", "--porcelain");
            if (exitCode != 0)
            {
                return new WorktreeAuditResult(
                    IsClean: false,
                    FailureReason: $"Grant audit failed: git status --porcelain failed (exit code {exitCode}): {stderr.Trim()}");
            }

            var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
            {
                return new WorktreeAuditResult(IsClean: true, FailureReason: null);
            }

            const int maxListed = 10;
            var totalCount = lines.Length;
            var strayPaths = lines
                .Select(l => l.Length > 3 ? l[3..].Trim() : l)
                .Take(maxListed)
                .ToList();

            var overflow = totalCount - strayPaths.Count;
            var pathsFormatted = string.Join(", ", strayPaths);
            var reason = overflow > 0
                ? $"Grant audit failed: worktree carries {totalCount} uncommitted/stray path(s) outside declared outputs: {pathsFormatted} (+{overflow} more)."
                : $"Grant audit failed: worktree carries {totalCount} uncommitted/stray path(s) outside declared outputs: {pathsFormatted}.";

            return new WorktreeAuditResult(IsClean: false, FailureReason: reason);
        }
        catch (Exception ex)
        {
            return new WorktreeAuditResult(
                IsClean: false,
                FailureReason: $"Grant audit failed: exception running git status --porcelain ({ex.Message})");
        }
    }

    /// <summary>
    /// Tear down provisioned worktrees only once the run is Terminal — a Paused run must keep its
    /// tree for the resume, and this deliberately runs on the success path (not in a finally) so a
    /// crashed or cancelled run leaves the worker's tree intact too. Teardown never throws; a tree
    /// kept for uncommitted changes or a blocked removal is surfaced on the result, not swallowed.
    /// </summary>
    public static IReadOnlyList<WorktreeTeardownResult> TeardownIfTerminal(
        Domain.WorkflowStatus status, IReadOnlyList<ProvisionedWorktree> provisionedWorktrees)
    {
        if (status == Domain.WorkflowStatus.Terminal && provisionedWorktrees.Count > 0)
        {
            return
            [
                .. provisionedWorktrees
                    .Select(w => Teardown(w.Repository, w.WorktreePath))
                    .Where(r => r.Outcome != WorktreeTeardownOutcome.Removed)
            ];
        }

        return [];
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new WorktreeProvisioningException("could not start 'git' — is it installed and on PATH?");
        }
        catch (Win32Exception ex)
        {
            // Process.Start throws (rather than returning null) when the executable is not found. Map it
            // to the typed exception so Provision fails loud and clean, and Teardown's catch can turn it
            // into a reported blocked removal rather than throwing out of a completed run.
            throw new WorktreeProvisioningException(
                $"could not start 'git' — is it installed and on PATH? ({ex.Message})");
        }

        // Drain both streams concurrently before waiting: reading one to end while the other's buffer
        // fills would deadlock on a chatty git command.
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Task.WaitAll(stdout, stderr);
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
    }

    // Note: the match is commit-only (HEAD sha vs ref sha), not ref-name, so two refs pointing at the same commit match identically.
    private static bool IsRegisteredWorktreeForRef(string repository, string worktreePath, string reference)
    {
        var (refExit, refSha, _) = RunGit(repository, "rev-parse", "--verify", $"{reference}^{{commit}}");
        if (refExit != 0 || string.IsNullOrWhiteSpace(refSha))
        {
            return false;
        }
        refSha = refSha.Trim();

        var (listExit, listOut, _) = RunGit(repository, "worktree", "list", "--porcelain");
        if (listExit != 0 || string.IsNullOrWhiteSpace(listOut))
        {
            return false;
        }

        string? currentPath = null;
        string? currentHead = null;

        var lines = listOut.Split(['\r', '\n']);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (currentPath != null && currentHead != null)
                {
                    if (PathsEqual(currentPath, worktreePath) && string.Equals(currentHead, refSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                currentPath = trimmed["worktree ".Length..].Trim();
                currentHead = null;
            }
            else if (trimmed.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                currentHead = trimmed["HEAD ".Length..].Trim();
            }
        }

        if (currentPath != null && currentHead != null)
        {
            if (PathsEqual(currentPath, worktreePath) && string.Equals(currentHead, refSha, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool PathsEqual(string path1, string path2)
    {
        try
        {
            var full1 = NormalizeForComparison(Path.GetFullPath(path1));
            var full2 = NormalizeForComparison(Path.GetFullPath(path2));
            return string.Equals(full1, full2, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> never resolves symlinks, and on macOS the standard
    /// temp roots (<c>/var</c>, <c>/tmp</c>, <c>/etc</c>) are symlinks into <c>/private</c> — git
    /// prints the resolved spelling in <c>worktree list</c>, so a caller-supplied <c>/var/...</c>
    /// path must compare equal to git's <c>/private/var/...</c> or the idempotence check (#1023)
    /// can never recognise its own worktree there (#1103, fixing what was then a macOS CI failure;
    /// no longer exercised on any CI leg now that the matrix is Windows-only, #1405, but harmless to
    /// keep -- a Windows path never starts with <c>/private/</c>, so this is a no-op there).
    /// Accepted edge: on non-macOS, a literal <c>/private/</c>-rooted directory would compare
    /// equal to its stripped twin — a layout nothing here produces, priced below the original fix.
    /// </summary>
    internal static string NormalizeForComparison(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        return trimmed.StartsWith("/private/", StringComparison.Ordinal)
            ? trimmed["/private".Length..]
            : trimmed;
    }
}

/// <summary>What <see cref="WorktreeProvisioner.Teardown"/> did — the three honest outcomes.</summary>
public enum WorktreeTeardownOutcome
{
    /// <summary>The worktree was removed (or was already gone).</summary>
    Removed,

    /// <summary>Uncommitted changes were present, so the worktree was kept rather than discarded.</summary>
    KeptUncommitted,

    /// <summary><c>git worktree remove</c> could not complete — typically a still-held build output.</summary>
    RemovalBlocked,
}

/// <summary>
/// The result of a <see cref="WorktreeProvisioner.Teardown"/> — surfaced, never thrown, so a teardown
/// fault cannot fail a room that already reached Terminal. <paramref name="Detail"/> is null for a
/// clean removal and carries the reason otherwise.
/// </summary>
public sealed record WorktreeTeardownResult(WorktreeTeardownOutcome Outcome, string WorktreePath, string? Detail);

/// <summary>The result of a post-run grant audit on a provisioned worktree.</summary>
public sealed record WorktreeAuditResult(bool IsClean, string? FailureReason);

/// <summary>
/// A worktree provisioned for a run, held so <c>WorktreeProvisioner.Teardown</c> can be called on it
/// once the run reaches Terminal.
/// </summary>
public sealed record ProvisionedWorktree(string Repository, string WorktreePath);
