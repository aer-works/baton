using System.ComponentModel;
using System.Diagnostics;
using Baton.Accounting;

namespace Baton.Cli;

/// <summary>
/// Probes git for the two strings <see cref="RepositoryIdentity.From"/> derives an identity from — the
/// <c>origin</c> remote URL and the git <i>common</i> directory. Kept in the CLI, not in the engine,
/// for the same reason <see cref="WorkspaceHead"/> is: the engine stays git-agnostic, and
/// <see cref="RepositoryIdentity"/> itself is pure string work with no process in it.
/// </summary>
/// <remarks>
/// <b>Never throws, never blocks.</b> A missing git, a non-repository directory, or a hung probe all
/// resolve to <see langword="null"/>, which the settle site reads as "this work has no repository
/// identity" and records nothing — the same fail-open posture <see cref="CostLedgerStore"/>'s own
/// remarks state for the write itself, not restated here.
/// </remarks>
internal static class RepositoryIdentityResolver
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The canonical identity of the repository at <paramref name="workingDirectory"/>, or
    /// <see langword="null"/> when there is none to be had.
    /// </summary>
    public static async Task<RepositoryIdentity?> TryResolveAsync(
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var originUrl = await RunGitAsync(workingDirectory, cancellationToken, "config", "--get", "remote.origin.url")
            .ConfigureAwait(false);

        // --git-common-dir is what makes every worktree of one repository share one identity: a linked
        // worktree's own `.git` is a file pointing back here, and `--git-dir` would give the
        // per-worktree path instead, fragmenting the ledger one file per checkout.
        var commonDir = await RunGitAsync(workingDirectory, cancellationToken, "rev-parse", "--path-format=absolute", "--git-common-dir")
            .ConfigureAwait(false);

        return RepositoryIdentity.From(originUrl, commonDir);
    }

    /// <summary>Stdout of a git invocation, trimmed — or null on any non-zero exit, missing git, or timeout.</summary>
    private static async Task<string?> RunGitAsync(
        string workingDirectory, CancellationToken cancellationToken, params string[] arguments)
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return null;
            }

            var trimmed = stdout.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Every one of these means "no identity available", which is a legitimate answer here, not
            // a failure to report: git absent, the directory gone, or the probe outran its timeout.
            return null;
        }
    }
}
