using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baton.Accounting;
using Baton.Cli.Daemon;
using Baton.Domain;
using Baton.Mutation;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

internal sealed record LedgerGitResult(bool Started, int ExitCode, string Stdout, string Stderr);

internal interface ILedgerGitRunner
{
    Task<LedgerGitResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>Production local-git runner for settle metadata; no invocation here contacts a remote.</summary>
internal sealed class LedgerGitRunner : ILedgerGitRunner
{
    public async Task<LedgerGitResult> RunAsync(
        string workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("git did not start.");
        }
        catch (Win32Exception)
        {
            return new LedgerGitResult(false, -1, string.Empty, "git was not found on PATH.");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            return new LedgerGitResult(
                true,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process exited between cancellation and the kill, or the host cannot kill a tree.
        }
    }
}

/// <summary>
/// Collects the git/forge facts that only the CLI can see while a room settles (#1901 C1), then hands
/// plain values to the git-agnostic accounting store. The GitHub runner is injected so tests never
/// invoke a live client or network. Every failed optional probe yields absence, never a guessed value
/// and never a failed settlement.
/// </summary>
internal static partial class CostLedgerSettlementMetadata
{
    private static readonly TimeSpan DefaultSpawnTimeout = TimeSpan.FromSeconds(20);

    private sealed record BranchFacts(string? Issue, string? PullRequest);
    private sealed record RepositoryProbe(string WorkingDirectory, bool UsesRemoteBranchHead);

    /// <summary>Builds one optional metadata value per settled execution.</summary>
    public static async Task<IReadOnlyDictionary<string, CostLedgerExecutionMetadata>> BuildAsync(
        IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath,
        string ledgerFilePath,
        IGhCliRunner ghRunner,
        ILedgerGitRunner? gitRunner = null,
        CancellationToken cancellationToken = default,
        TimeSpan? spawnTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);
        ArgumentNullException.ThrowIfNull(ghRunner);
        gitRunner ??= new LedgerGitRunner();
        var effectiveSpawnTimeout = spawnTimeout ?? DefaultSpawnTimeout;
        if (effectiveSpawnTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(spawnTimeout), "The per-spawn timeout must be positive.");
        }

        var bindings = await TryReadBindingsAsync(roomDirectoryPath, cancellationToken).ConfigureAwait(false);
        var priorRows = await CostLedgerStore.ReadAllAsync(ledgerFilePath, cancellationToken).ConfigureAwait(false);
        var recordedRoom = BatonPaths.RecordKey(roomDirectoryPath);
        var priorIssue = priorRows.LastOrDefault(row => row.Room is not null
            && BatonPaths.RecordKeyComparer.Equals(row.Room, recordedRoom)
            && row.Issue is { Length: > 0 })?.Issue;

        var requests = new Dictionary<string, ExecutionRequest>(StringComparer.Ordinal);
        var succeeded = new HashSet<string>(StringComparer.Ordinal);
        var lastSucceededByWorker = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionRequestAccepted accepted })
            {
                requests[accepted.Request.ExecutionId.Value] = accepted.Request;
            }
            else if (entry is LogEntry.FlowLogEntry { Event: FlowEvent.ExecutionSucceeded completed }
                && requests.TryGetValue(completed.ExecutionId.Value, out var request))
            {
                succeeded.Add(completed.ExecutionId.Value);
                lastSucceededByWorker[request.Worker] = completed.ExecutionId.Value;
            }
        }

        var branchFactsByBranch = new Dictionary<string, BranchFacts>(StringComparer.Ordinal);
        BranchFacts? roomFacts = null;
        var result = new Dictionary<string, CostLedgerExecutionMetadata>(StringComparer.Ordinal);

        foreach (var (executionId, request) in requests)
        {
            bindings.TryGetValue(request.Worker, out var binding);
            var repositoryProbe = ResolveRepositoryProbe(binding);
            var workingDirectory = repositoryProbe?.WorkingDirectory;
            var branch = binding?.WorkspaceBranch
                ?? await TryReadBranchAsync(workingDirectory, gitRunner, cancellationToken, effectiveSpawnTimeout).ConfigureAwait(false);

            BranchFacts? branchFacts = null;
            if (branch is { Length: > 0 })
            {
                if (!branchFactsByBranch.TryGetValue(branch, out branchFacts))
                {
                    branchFacts = new BranchFacts(
                        IssueFromBranch(branch),
                        await TryFindPullRequestAsync(ghRunner, workingDirectory, branch, cancellationToken, effectiveSpawnTimeout)
                            .ConfigureAwait(false));
                    branchFactsByBranch[branch] = branchFacts;
                }

                roomFacts ??= branchFacts;
            }

            branchFacts ??= roomFacts;
            var issue = branchFacts?.Issue ?? priorIssue;
            var pullRequest = branchFacts?.PullRequest;

            CostLedgerExecutionMetadata? diff = null;
            if (binding is { DeliversBranch: true }
                && succeeded.Contains(executionId)
                && lastSucceededByWorker.GetValueOrDefault(request.Worker) == executionId
                && branch is { Length: > 0 })
            {
                diff = await TryReadPushedDiffAsync(
                    workingDirectory,
                    branch,
                    repositoryProbe?.UsesRemoteBranchHead == true,
                    gitRunner,
                    cancellationToken,
                    effectiveSpawnTimeout).ConfigureAwait(false);
            }

            result[executionId] = new CostLedgerExecutionMetadata(
                Issue: issue,
                PullRequest: pullRequest,
                FilesChanged: diff?.FilesChanged,
                Additions: diff?.Additions,
                Deletions: diff?.Deletions,
                TestFilesChanged: diff?.TestFilesChanged);
        }

        // A worker without a resolvable directory can still inherit the one branch another binding in
        // this room recorded. Fill that fallback only after every binding has had a chance to provide it.
        if (roomFacts is not null || priorIssue is not null)
        {
            foreach (var executionId in result.Keys.ToList())
            {
                var metadata = result[executionId];
                result[executionId] = metadata with
                {
                    Issue = metadata.Issue ?? roomFacts?.Issue ?? priorIssue,
                    PullRequest = metadata.PullRequest ?? roomFacts?.PullRequest,
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Best-effort named-branch capture used both at dispatch (to preserve the source branch on the
    /// room) and at settle for older rooms without that stamp.
    /// </summary>
    public static Task<string?> TryReadBranchAsync(
        string? workingDirectory, CancellationToken cancellationToken = default) =>
        TryReadBranchAsync(workingDirectory, new LedgerGitRunner(), cancellationToken, DefaultSpawnTimeout);

    internal static async Task<string?> TryReadBranchAsync(
        string? workingDirectory,
        ILedgerGitRunner gitRunner,
        CancellationToken cancellationToken = default,
        TimeSpan? spawnTimeout = null)
    {
        try
        {
            var result = await TryRunGitAsync(
                workingDirectory,
                ["rev-parse", "--abbrev-ref", "HEAD"],
                gitRunner,
                cancellationToken,
                spawnTimeout ?? DefaultSpawnTimeout).ConfigureAwait(false);
            if (result is not { ExitCode: 0 } || string.IsNullOrWhiteSpace(result.Value.Output))
            {
                return null;
            }

            var branch = result.Value.Output.Trim();
            return string.Equals(branch, "HEAD", StringComparison.Ordinal) ? null : branch;
        }
        catch (Exception)
        {
            // Dispatch accounting is optional: an arbitrary runner failure must not refuse the dispatch.
            return null;
        }
    }

    private static async Task<IReadOnlyDictionary<string, WorkerBindingConfigEntry>> TryReadBindingsAsync(
        string roomDirectoryPath, CancellationToken cancellationToken)
    {
        try
        {
            return await WorkerBindingConfigParser
                .LoadFromFileAsync(BatonPaths.RoomBindingsFile(roomDirectoryPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkerBindingConfigException)
        {
            return new Dictionary<string, WorkerBindingConfigEntry>(StringComparer.Ordinal);
        }
    }

    private static RepositoryProbe? ResolveRepositoryProbe(WorkerBindingConfigEntry? binding)
    {
        if (binding?.WorkingDirectory is { Length: > 0 } workingDirectory
            && Directory.Exists(workingDirectory))
        {
            return new RepositoryProbe(workingDirectory, UsesRemoteBranchHead: false);
        }

        // A terminal audited run normally tears its clean worktree down before Program performs the
        // settle-time ledger append. The provisioner preserved the source repository on the binding,
        // and DeliveryVerifier fetched origin/<branch> before removal, so that remote-tracking ref is
        // the same pushed commit the now-missing worktree called HEAD.
        var sourceRepository = binding?.WorktreeSourceRepository ?? binding?.Worktree?.Repository;
        return sourceRepository is { Length: > 0 } && Directory.Exists(sourceRepository)
            ? new RepositoryProbe(sourceRepository, UsesRemoteBranchHead: true)
            : null;
    }

    private static string? IssueFromBranch(string branch)
    {
        var match = LeadingIssuePattern().Match(branch);
        return match.Success ? match.Groups["issue"].Value : null;
    }

    private static async Task<string?> TryFindPullRequestAsync(
        IGhCliRunner ghRunner,
        string? workingDirectory,
        string branch,
        CancellationToken cancellationToken,
        TimeSpan spawnTimeout)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        GhCliResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(spawnTimeout);
            result = await ghRunner.RunAsync(
                    workingDirectory,
                    ["pr", "list", "--head", branch, "--state", "all", "--json", "number", "--limit", "1"],
                    timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }

        if (!result.Started || result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() == 0
                || !document.RootElement[0].TryGetProperty("number", out var number)
                || !number.TryGetInt32(out var parsed))
            {
                return null;
            }

            return parsed.ToString(CultureInfo.InvariantCulture);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<CostLedgerExecutionMetadata?> TryReadPushedDiffAsync(
        string? workingDirectory,
        string branch,
        bool usesRemoteBranchHead,
        ILedgerGitRunner gitRunner,
        CancellationToken cancellationToken,
        TimeSpan spawnTimeout)
    {
        var remoteBranch = $"refs/remotes/origin/{branch}";
        var diffHead = usesRemoteBranchHead ? $"origin/{branch}" : "HEAD";
        var remote = await TryRunGitAsync(
            workingDirectory, ["rev-parse", "--verify", remoteBranch], gitRunner, cancellationToken, spawnTimeout).ConfigureAwait(false);
        if (remote is not { ExitCode: 0 })
        {
            return null;
        }

        var pushed = await TryRunGitAsync(
            workingDirectory, ["merge-base", "--is-ancestor", diffHead, remoteBranch], gitRunner, cancellationToken, spawnTimeout)
            .ConfigureAwait(false);
        if (pushed is not { ExitCode: 0 })
        {
            return null;
        }

        var numStat = await TryRunGitAsync(
            workingDirectory, ["diff", "--numstat", $"origin/main...{diffHead}"], gitRunner, cancellationToken, spawnTimeout)
            .ConfigureAwait(false);
        return numStat is { ExitCode: 0 }
            ? TryParseNumStat(numStat.Value.Output)
            : null;
    }

    private static CostLedgerExecutionMetadata? TryParseNumStat(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new CostLedgerExecutionMetadata(
                FilesChanged: 0,
                Additions: 0,
                Deletions: 0,
                TestFilesChanged: 0);
        }

        var filesChanged = 0;
        var additions = 0;
        var deletions = 0;
        var testFilesChanged = 0;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length != 3
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var fileAdditions)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var fileDeletions))
            {
                return null;
            }

            try
            {
                filesChanged = checked(filesChanged + 1);
                additions = checked(additions + fileAdditions);
                deletions = checked(deletions + fileDeletions);
                if (IsTestPath(parts[2]))
                {
                    testFilesChanged = checked(testFilesChanged + 1);
                }
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return new CostLedgerExecutionMetadata(
            FilesChanged: filesChanged,
            Additions: additions,
            Deletions: deletions,
            TestFilesChanged: testFilesChanged);
    }

    private static bool IsTestPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('"');
        return normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)?> TryRunGitAsync(
        string? workingDirectory,
        IReadOnlyList<string> args,
        ILedgerGitRunner gitRunner,
        CancellationToken cancellationToken,
        TimeSpan spawnTimeout)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(spawnTimeout);
            var result = await gitRunner.RunAsync(workingDirectory, args, timeout.Token)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            return result.Started ? (result.ExitCode, result.Stdout) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<issue>[1-9]\d*)-", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIssuePattern();

}
