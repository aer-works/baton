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
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new LedgerGitResult(
                true,
                process.ExitCode,
                await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false));
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
    private sealed record BranchFacts(string? Issue, string? PullRequest);
    private sealed record RepositoryProbe(string WorkingDirectory, bool UsesRemoteBranchHead);

    /// <summary>Builds one optional metadata value per settled execution.</summary>
    public static async Task<IReadOnlyDictionary<string, CostLedgerExecutionMetadata>> BuildAsync(
        IReadOnlyList<LogEntry> entries,
        string roomDirectoryPath,
        string ledgerFilePath,
        IGhCliRunner ghRunner,
        ILedgerGitRunner? gitRunner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(roomDirectoryPath);
        ArgumentException.ThrowIfNullOrEmpty(ledgerFilePath);
        ArgumentNullException.ThrowIfNull(ghRunner);
        gitRunner ??= new LedgerGitRunner();

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
                ?? await TryReadBranchAsync(workingDirectory, gitRunner, cancellationToken).ConfigureAwait(false);

            BranchFacts? branchFacts = null;
            if (branch is { Length: > 0 })
            {
                if (!branchFactsByBranch.TryGetValue(branch, out branchFacts))
                {
                    branchFacts = new BranchFacts(
                        IssueFromBranch(branch),
                        await TryFindPullRequestAsync(ghRunner, workingDirectory, branch, cancellationToken)
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
                    cancellationToken).ConfigureAwait(false);
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
        TryReadBranchAsync(workingDirectory, new LedgerGitRunner(), cancellationToken);

    internal static async Task<string?> TryReadBranchAsync(
        string? workingDirectory, ILedgerGitRunner gitRunner, CancellationToken cancellationToken = default)
    {
        var result = await TryRunGitAsync(
            workingDirectory, ["rev-parse", "--abbrev-ref", "HEAD"], gitRunner, cancellationToken).ConfigureAwait(false);
        if (result is not { ExitCode: 0 } || string.IsNullOrWhiteSpace(result.Value.Output))
        {
            return null;
        }

        var branch = result.Value.Output.Trim();
        return string.Equals(branch, "HEAD", StringComparison.Ordinal) ? null : branch;
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
        CancellationToken cancellationToken)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        GhCliResult result;
        try
        {
            result = await ghRunner.RunAsync(
                workingDirectory,
                ["pr", "list", "--head", branch, "--state", "all", "--json", "number", "--limit", "1"],
                cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var remoteBranch = $"refs/remotes/origin/{branch}";
        var diffHead = usesRemoteBranchHead ? $"origin/{branch}" : "HEAD";
        var remote = await TryRunGitAsync(
            workingDirectory, ["rev-parse", "--verify", remoteBranch], gitRunner, cancellationToken).ConfigureAwait(false);
        if (remote is not { ExitCode: 0 })
        {
            return null;
        }

        var pushed = await TryRunGitAsync(
            workingDirectory, ["merge-base", "--is-ancestor", diffHead, remoteBranch], gitRunner, cancellationToken)
            .ConfigureAwait(false);
        if (pushed is not { ExitCode: 0 })
        {
            return null;
        }

        var shortStat = await TryRunGitAsync(
            workingDirectory, ["diff", "--shortstat", $"origin/main...{diffHead}"], gitRunner, cancellationToken)
            .ConfigureAwait(false);
        var numStat = await TryRunGitAsync(
            workingDirectory, ["diff", "--numstat", $"origin/main...{diffHead}"], gitRunner, cancellationToken)
            .ConfigureAwait(false);
        if (shortStat is not { ExitCode: 0 } || numStat is not { ExitCode: 0 })
        {
            return null;
        }

        var filesChanged = ReadStat(ShortStatFilesPattern(), shortStat.Value.Output);
        var additions = ReadStat(ShortStatAdditionsPattern(), shortStat.Value.Output);
        var deletions = ReadStat(ShortStatDeletionsPattern(), shortStat.Value.Output);
        var testFilesChanged = numStat.Value.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 3))
            .Count(parts => parts.Length == 3 && IsTestPath(parts[2]));

        return new CostLedgerExecutionMetadata(
            FilesChanged: filesChanged,
            Additions: additions,
            Deletions: deletions,
            TestFilesChanged: testFilesChanged);
    }

    private static int ReadStat(Regex pattern, string shortStat)
    {
        var match = pattern.Match(shortStat);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count) ? count : 0;
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
        CancellationToken cancellationToken)
    {
        if (workingDirectory is null)
        {
            return null;
        }

        try
        {
            var result = await gitRunner.RunAsync(workingDirectory, args, cancellationToken).ConfigureAwait(false);
            return result.Started ? (result.ExitCode, result.Stdout) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^(?<issue>[1-9]\d*)-", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIssuePattern();

    [GeneratedRegex(@"(?<count>\d+) files? changed", RegexOptions.CultureInvariant)]
    private static partial Regex ShortStatFilesPattern();

    [GeneratedRegex(@"(?<count>\d+) insertions?\(\+\)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortStatAdditionsPattern();

    [GeneratedRegex(@"(?<count>\d+) deletions?\(-\)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortStatDeletionsPattern();
}
