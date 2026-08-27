using System.Diagnostics;
using Aer.Adapters;
using Aer.Cli.Tests.TestSupport;
using Aer.Flow.Domain;

namespace Aer.Cli.Tests;

/// <summary>
/// R1 (#1354/#1380, finding 9's item 1+2): the acceptance path the prior PR's own tests never
/// exercised — a real audited role dispatch against a real git workspace, not a bare temp directory
/// (<c>RoleDispatchTests</c>' old worktree test built one with <c>Directory.CreateDirectory</c> and
/// could never actually provision) and not a happy-path adapter that never enters the audited branch at
/// all (<c>DispatchCommandEndToEndTests</c>' <c>--output</c> test dispatches to an adapter the registry
/// does not know, so the grant never flips). <see cref="ContractOutputWorkerAdapter"/> is registered
/// under the key <c>"agy"</c> here so <c>RoleDispatch.ToBinding</c>'s
/// <c>WorkerAdapterRegistry.Default</c> lookup resolves the real <c>AgyWorkerAdapter</c>'s
/// <c>WithheldWritesReachTheOutbox</c> (false) and flips the grant to <c>AuditedNotEnforced</c>, while
/// the process actually dispatched is still this file's fake — no live vendor needed.
/// </summary>
[Collection(WorkerCatalogEnvCollection.Name)]
public sealed class DispatchAuditedWorktreeAcceptanceTests : IDisposable
{
    private readonly string? _priorRoles = Environment.GetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable);
    private readonly string? _priorTiers = Environment.GetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable);

    public DispatchAuditedWorktreeAcceptanceTests()
    {
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"));
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, _priorRoles);
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable, _priorTiers);
    }

    [Fact]
    public async Task Dispatching_review_on_agy_against_a_real_git_workspace_auto_provisions_and_satisfies_the_contract_with_output()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-e2e-{Guid.NewGuid():N}");
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var outputPath = Path.Combine(testRoot, "report-out.md");
            var adapters = await AgyFakeAdaptersAsync(testRoot);

            var options = new DispatchOptions(
                "review", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace, OutputPath: outputPath);

            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
            Assert.True(File.Exists(outputPath), "the --output copy of report.md should have landed");

            // The binding that actually ran was audited and provisioned, not enforced against the
            // caller's own workspace directly — the whole point of R1.
            var bindingsPath = Path.Combine(roomDirectory, "bindings.json");
            Assert.Contains(
                "AuditedNotEnforced", await File.ReadAllTextAsync(bindingsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_review_on_agy_prints_the_audited_write_grant_not_a_bare_write()
    {
        // #1355: the printed grant line has to name the audited-not-enforced write it actually
        // resolved to, not just "write" -- otherwise an invoking agent relaying the line to its own
        // permission layer under-reports what the run really carried.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-grant-line-{Guid.NewGuid():N}");
        var originalOut = Console.Out;
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            await InitGitRepoAsync(workspace);

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var adapters = await AgyFakeAdaptersAsync(testRoot, translatesGrants: true);

            var options = new DispatchOptions("review", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace);

            using var consoleOutput = new StringWriter();
            Console.SetOut(consoleOutput);
            await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);
            Console.SetOut(originalOut);

            Assert.Contains(
                "Grant: read, write (workspace-wide inside an isolated worktree; audited against declared outputs after the run), no-shell, no-network",
                consoleOutput.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    [Fact]
    public async Task Dispatching_review_on_agy_against_a_workspace_that_is_itself_a_worktree_with_an_untracked_file_still_succeeds()
    {
        // The red test for finding 1/R1: before this fix, IsWorktree(workspace) == true routed the
        // caller's OWN directory in as WorkingDirectory (stamped IsWorktree: true without this run
        // having provisioned it), so the post-run audit inspected the caller's own untracked file and
        // failed Permanent. R1 provisions a fresh worktree regardless of the caller's directory shape,
        // so the caller's own dirt must be irrelevant to the outcome.
        var testRoot = Path.Combine(Path.GetTempPath(), $"dispatch-agy-worktree-e2e-{Guid.NewGuid():N}");
        try
        {
            var mainRepo = Path.Combine(testRoot, "main-repo");
            await InitGitRepoAsync(mainRepo);

            var workspace = Path.Combine(testRoot, "caller-worktree");
            await RunGitAsync(mainRepo, "worktree", "add", "--detach", workspace, "HEAD");
            // The caller's own uncommitted dirt — untracked, never staged or committed. Under the old
            // behaviour this alone was enough to fail the post-run audit.
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "operators-scratch-file.txt"), "not the worker's business",
                TestContext.Current.CancellationToken);

            var specPath = await WriteSpecAsync(testRoot, "Review the change.");
            var roomDirectory = Path.Combine(testRoot, "task");
            var adapters = await AgyFakeAdaptersAsync(testRoot);

            var options = new DispatchOptions("review", specPath, roomDirectory, Adapter: "agy", WorkspaceDirectory: workspace);

            var result = await DispatchCommand.ExecuteAsync(options, adapters, TestContext.Current.CancellationToken);

            Assert.Equal(WorkflowStatus.Terminal, result.State.Status);
            var step = Assert.Single(result.State.Steps);
            Assert.Equal(StepStatus.Succeeded, step.Status);
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(testRoot);
        }
    }

    /// <param name="translatesGrants">
    /// F2/F3: the printed-grant-line test needs the bound "agy" adapter to actually consume a grant
    /// (<see cref="IPermissionGrantTranslator"/>) or <see cref="DispatchCommand"/> now prints nothing
    /// for it. The other two tests here assert on run outcome, not the grant line, so they keep the
    /// plain <see cref="ContractOutputWorkerAdapter"/> that sits outside that population -- narrower
    /// than opting every acceptance test here into WorkerBindingResolver's grant-consuming refusal
    /// checks for no reason.
    /// </param>
    private static async Task<IReadOnlyDictionary<string, IWorkerAdapter>> AgyFakeAdaptersAsync(
        string testRoot, bool translatesGrants = false)
    {
        // A minimal conforming ReviewVerdict (decision 0043: the engine checks only that it PARSES as
        // one — ReviewedRef required, empty Findings valid).
        var verdictFixture = Path.Combine(testRoot, "verdict-fixture.json");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            verdictFixture, """{"reviewedRef":"HEAD","findings":[]}""", TestContext.Current.CancellationToken);

        var outputFixtures = new Dictionary<string, string> { ["verdict.json"] = verdictFixture };
        IWorkerAdapter agyAdapter = translatesGrants
            ? new GrantConsumingContractOutputWorkerAdapter(satisfyOutputs: true, outputFixtures)
            : new ContractOutputWorkerAdapter(satisfyOutputs: true, outputFixtures);

        return new Dictionary<string, IWorkerAdapter> { ["agy"] = agyAdapter };
    }

    private static async Task<string> WriteSpecAsync(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "spec.md");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private static async Task InitGitRepoAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await RunGitAsync(directory, "init", "-q");
        // -c identity keeps the commit independent of any (absent) global git config on the runner.
        await RunGitAsync(
            directory, "-c", "user.email=test@example.invalid", "-c", "user.name=Test",
            "commit", "--allow-empty", "-q", "-m", "base");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start git — is it on PATH? These tests need git.");
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr.Trim()}");
        }
    }
}
