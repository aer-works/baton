namespace Baton.Status;

/// <summary>
/// The frozen read of every baton-config environment variable a production hot path used to
/// re-read on every access (#1496). One process-wide read, taken once and never mutated, replaces
/// the "resolve, never capture" discipline <see cref="BatonPaths"/> used to document — that
/// discipline is what forced #1491's <c>SerializedEnvironmentCollection</c>: any test that flipped
/// one of these variables could race a production reader re-deriving the same value mid-process.
/// Freezing removes the race at its root; <see cref="BeginScope"/> gives tests an explicit,
/// non-mutating way to supply a different set of values instead.
/// </summary>
/// <remarks>
/// <para>
/// Every field here is the raw string exactly as <c>Environment.GetEnvironmentVariable</c> would
/// return it — <c>null</c> when unset. Parsing (bounds-clamping, bool coercion, blank-as-unset) stays
/// at each consumer, unchanged; this type's only job is freezing the read, not the interpretation.
/// </para>
/// <para>
/// <b>Only two fields from the original #1496 fold, not the six the task named — this is the
/// canonical record of why, the four readers' own "#1496 exempt" comments only point back here.</b>
/// <see cref="BatonPaths"/>'s <c>BATON_HOME</c> read and <c>McpCommand.cs</c>'s <c>BATON_OUTPUT_DIR</c>
/// read are folded here. (A third field, <see cref="RepoOverride"/>, was added later by #1645 for a
/// brand-new reader — <c>Baton.Cli.InstalledVersionDrift</c> — with no such history to fight; it never
/// needed the fold/revert dance below.)
/// The other four direct readers were tried against this snapshot and reverted, each because its own
/// test suite sets an env var per <c>[Fact]</c> and expects the very next call to observe it — the
/// resolution behaviour IS the subject under test there, which a frozen-at-first-access snapshot
/// structurally cannot support:
/// <list type="bullet">
/// <item><c>RoomRetentionSweep.cs</c> (<c>IsEnabled</c>/<c>IsPruneEnabled</c>/<c>GetInterval</c>/
///   <c>GetThresholdBytes</c>/<c>GetPruneGrace</c>): 5 of 12 <c>RoomRetentionSweepTests</c> failed
///   under the fold;</item>
/// <item><c>ClaudeWorkerAdapter.cs</c>'s <c>BatonClaudeConfigRootVariable</c> read:
///   <c>ClaudeWorkerAdapterTests.Claude_config_root_set_injects_CLAUDE_CONFIG_DIR_for_batch_and_gate</c>
///   failed under the fold;</item>
/// <item><c>WorkerRoleCatalog.cs</c>'s <c>ResolvePath</c>: 13 of 20 <c>WorkerRoleCatalogTests</c>
///   (and <c>CatalogNamespaceTests</c>) failed under the fold;</item>
/// <item><c>WorkflowTemplateCatalog.cs</c>'s <c>ResolvePath</c>: 20 of 21 tests in that assembly
///   failed under the fold — same shape as <c>WorkerRoleCatalog</c>'s.</item>
/// </list>
/// Adding a field here for one of those four with no reader consuming it would be exactly the
/// unpinned duplication <c>record-once</c> forbids (the field's literal env-var-name string would
/// drift silently from the reader's own public const the moment either one is renamed) — so the
/// fields stay absent until a follow-up PR (issue #1524) actually folds a given reader and adds its
/// field in the same change.
/// </para>
/// <para>
/// <b>What is deliberately absent for a different reason.</b> Two families of direct env read stay
/// on <c>Environment.GetEnvironmentVariable</c> and are never candidates for this type: the
/// genuinely-once reads at the top of <c>Program.cs</c> (read a single time before any work starts,
/// never re-read, so there is no per-access race to remove), and <c>InheritedEnvironment.cs</c>'s
/// child-process allowlist (that reader is about the *live* environment a spawned worker should
/// inherit, not AER's own config — freezing it would silently stop a worker from inheriting a
/// variable an operator exports mid-session).
/// </para>
/// </remarks>
public sealed record BatonEnvironmentSnapshot(
    string? HomeOverride,
    string? McpOutputDirectory,
    string? RepoOverride = null)
{
    private static readonly Lazy<BatonEnvironmentSnapshot> ProcessSnapshot = new(CaptureFromEnvironment);

    private static readonly AsyncLocal<BatonEnvironmentSnapshot?> AmbientOverride = new();

    /// <summary>
    /// Every field null (nothing overridden) — a base for a test's <c>with</c> expression that only
    /// cares about one or two fields and wants to be explicit about the rest, rather than inheriting
    /// whatever <see cref="Current"/> happens to hold on the machine running it.
    /// </summary>
    public static readonly BatonEnvironmentSnapshot Blank = new(
        HomeOverride: null,
        McpOutputDirectory: null,
        RepoOverride: null);

    /// <summary>
    /// The snapshot every reader resolves against: an explicit <see cref="BeginScope"/> override on
    /// the calling async flow when one is active, otherwise the one process snapshot captured on
    /// first access. Never re-reads the environment after that first capture.
    /// </summary>
    public static BatonEnvironmentSnapshot Current => AmbientOverride.Value ?? ProcessSnapshot.Value;

    private static BatonEnvironmentSnapshot CaptureFromEnvironment() => new(
        HomeOverride: Environment.GetEnvironmentVariable(BatonPaths.HomeEnvironmentVariable),
        // "BATON_OUTPUT_DIR" -- mirrors the literal McpCommand.cs reads. Program.cs reads the same
        // variable name for the hook-check commands; that read stays direct (see the type remarks) --
        // this field only covers the McpCommand.cs per-access read.
        McpOutputDirectory: Environment.GetEnvironmentVariable("BATON_OUTPUT_DIR"),
        // "BATON_REPO" -- mirrors the literal Baton.Cli.InstalledVersionDrift.RepoEnvironmentVariable
        // (#1645). That type lives downstream of this project (Baton.Cli depends on Baton, not the
        // reverse), the same reason McpOutputDirectory's name above is a duplicated literal rather than
        // a shared const.
        RepoOverride: Environment.GetEnvironmentVariable("BATON_REPO"));

    /// <summary>
    /// Test-only seam (via <c>InternalsVisibleTo</c>): makes <paramref name="snapshot"/> the ambient
    /// override for every <see cref="Current"/> read on the calling async flow until the returned
    /// scope is disposed, restoring whatever was ambient before. Never mutates process environment
    /// variables, so a scoped test needs no <c>SerializedEnvironmentCollection</c> enrollment and runs
    /// parallel-safe with everything else.
    /// </summary>
    /// <remarks>
    /// <b>Flows further than it might look.</b> <see cref="AsyncLocal{T}"/> flows through
    /// <c>async</c>/<c>await</c>, <c>Task.Run</c>, <em>and</em> a manually-created
    /// <see cref="System.Threading.Thread"/> — <c>Thread.Start</c> captures and runs on the calling
    /// thread's <see cref="System.Threading.ExecutionContext"/> by default, so code on a raw thread
    /// still sees an active scope. The real boundary is
    /// <see cref="System.Threading.ExecutionContext.SuppressFlow"/> (and its sibling opt-out,
    /// <see cref="System.Threading.Thread.UnsafeStart()"/>): code started from inside a suppressed
    /// region — whatever kind of thread it runs on — sees the process snapshot regardless of an
    /// active scope on the thread that suppressed flow. See <c>BatonEnvironmentSnapshotTests</c> for
    /// the tripwire documentation of both facts.
    /// </remarks>
    internal static IDisposable BeginScope(BatonEnvironmentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Scope(snapshot);
    }

    private sealed class Scope : IDisposable
    {
        private readonly BatonEnvironmentSnapshot? _prior;
        private bool _disposed;

        public Scope(BatonEnvironmentSnapshot snapshot)
        {
            _prior = AmbientOverride.Value;
            AmbientOverride.Value = snapshot;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            AmbientOverride.Value = _prior;
        }
    }
}
