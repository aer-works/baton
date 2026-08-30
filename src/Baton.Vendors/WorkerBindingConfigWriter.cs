using System.Text.Json;

namespace Baton.Vendors;

/// <summary>
/// Writes a worker-binding config to a file (#153) — the first
/// bindings write path anywhere in the stack, and the counterpart to
/// <see cref="WorkerBindingConfigParser"/>.
/// <para>
/// <b>Placement decision of record:</b> the writer lives here, beside its parser in
/// <c>Baton.Vendors</c>, not in a caller's assembly — the bindings shape
/// (adapter names, <see cref="Baton.Domain.WorkerContract"/>, prompt/timeout/model/permission
/// scope) lives entirely in this assembly (Adapter Isolation, the repo's own architecture rule),
/// exactly mirroring <c>Baton.Templates.WorkflowDefinitionWriter</c>'s placement reasoning
/// beside <c>WorkflowDefinitionParser</c> for templates (M16 Phase 1). Originally written for the
/// desktop authoring surface (deleted, #1412); the daemon caller went with the daemon's HTTP
/// surface (#1420) — today's sole production caller is <c>DispatchCommand</c>.
/// </para>
/// <para>
/// <b>Validation decision of record:</b> there is no separate <c>WorkerBindingConfigValidator</c> —
/// <see cref="WorkerBindingConfigParser.Parse"/>'s own field checks (non-blank <c>Adapter</c>, a
/// present <c>Contract</c>, non-blank <c>PromptTemplate</c>) are this format's only validation.
/// <see cref="Serialize"/> proves them by round-tripping its own output through that exact parser
/// before ever returning it, so "write nothing on failure" holds the same way
/// <c>Baton.Templates.WorkflowDefinitionWriter.Serialize</c> holds it via
/// <c>WorkflowDefinitionValidator.Validate</c> — just using the parser itself as the validation
/// step, since this format has no separate one.
/// </para>
/// <para>
/// Output is indented for the same reason a template's is: a hand-editable file. The round-trip
/// bar is parse-level fidelity, never byte-level.
/// </para>
/// </summary>
public static class WorkerBindingConfigWriter
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    /// How long the replace keeps retrying. Wall-clock rather than an attempt count, and the same five
    /// seconds its two siblings use — <c>AtomicLaunchConfigWriter</c> documents why, and #931 is the
    /// measurement; #1266 is this writer paying the same tuition a second time.
    /// </summary>
    private static readonly TimeSpan DefaultReplaceRetryBudget = TimeSpan.FromSeconds(5);

    /// <summary>Backoff ceiling, so even a long budget keeps retrying often.</summary>
    private const double MaxReplaceBackoffMs = 250;

    /// <summary>Serializes <paramref name="config"/> as indented bindings JSON, validating it by parsing it back first.</summary>
    /// <exception cref="WorkerBindingConfigException">
    /// <paramref name="config"/> fails to round-trip through <see cref="WorkerBindingConfigParser.Parse"/>
    /// (e.g. a blank <c>Adapter</c> or <c>PromptTemplate</c> on some entry).
    /// </exception>
    public static string Serialize(IReadOnlyDictionary<string, WorkerBindingConfigEntry> config)
    {
        var json = JsonSerializer.Serialize(config, IndentedOptions);
        WorkerBindingConfigParser.Parse(json);
        return json;
    }

    /// <summary>
    /// Persists <paramref name="config"/> as bindings JSON at <paramref name="bindingsFilePath"/>,
    /// creating parent directories as needed — the same shape as
    /// <c>Baton.Templates.WorkflowDefinitionWriter.SaveToFileAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Atomic, per 0057 rule 1</b>, and not merely for tidiness — that record's Context is where
    /// the failure a truncate-then-write leaves behind is set out, and why the room-events lock the
    /// daemon's writers take does not cover it.
    /// </para>
    /// <para>
    /// The staging name is unique per call, so two writers racing the same target cannot land in each
    /// other's temp file — the same reasoning <c>MaterializeRoomBindings</c> states for its own copy.
    /// The lock is still what stops those two writers losing each other's <em>updates</em>; that is a
    /// different question from this one, and 0057 keeps them apart deliberately.
    /// </para>
    /// </remarks>
    /// <exception cref="WorkerBindingConfigException">
    /// <paramref name="config"/> fails to round-trip through the parser; nothing is written.
    /// </exception>
    public static Task SaveToFileAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        string bindingsFilePath,
        CancellationToken cancellationToken = default)
        => SaveToFileAsync(config, bindingsFilePath, DefaultReplaceRetryBudget, cancellationToken);

    /// <summary>
    /// The budget-injecting form of <see cref="SaveToFileAsync(IReadOnlyDictionary{string, WorkerBindingConfigEntry}, string, CancellationToken)"/>.
    /// Internal so a test can force the exhaustion path with a tiny budget rather than waiting out the
    /// production one — the same seam <c>SnapshotBinder.PersistAsync</c> exposes, for the same reason.
    /// Production always calls the public overload.
    /// </summary>
    internal static async Task SaveToFileAsync(
        IReadOnlyDictionary<string, WorkerBindingConfigEntry> config,
        string bindingsFilePath,
        TimeSpan replaceRetryBudget,
        CancellationToken cancellationToken = default)
    {
        var json = Serialize(config);

        var directory = Path.GetDirectoryName(bindingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var staging = bindingsFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(staging, json, cancellationToken).ConfigureAwait(false);
            await ReplaceWithRetryAsync(staging, bindingsFilePath, replaceRetryBudget, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A staging file that survives means the move never happened, so the target is whatever
            // it already was — which is the outcome this method promises on failure. Swallowed
            // narrowly: failing to clean up a temp file must not turn a successful write into an
            // exception, and must not mask the real one on the failure path.
            if (File.Exists(staging))
            {
                try { File.Delete(staging); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="staging"/>, retrying briefly while a
    /// reader holds the target open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is being retried, why it only bites on Windows, and why absorbing it here is the right
    /// end of the trade are all in 0057's Consequences, measured. This absorbs it.
    /// </para>
    /// <para>
    /// <b>Bounded by elapsed time, not attempt count</b> (#1266) — the anti-starvation reason
    /// <c>AtomicLaunchConfigWriter</c> documents, which <c>SnapshotBinder.PersistAsync</c> already
    /// applies for the same rename. This method shipped with the pre-#931 shape and reproduced the
    /// failure that one was measured to have: 20 attempts 10ms apart is ~200ms only when the attempts
    /// get scheduled, and under full-suite load they do not.
    /// </para>
    /// <para>
    /// The retry is not an artifact of how the repo's own readers open the file, and cannot be
    /// removed by changing them — see 0057's Consequences for the measurement, and #1267 for the
    /// place that lesson was drawn wrongly the first time. Foreign handles (a virus scanner, an
    /// indexer) are default-share by definition and are what it exists for.
    /// </para>
    /// <para>
    /// <b>It retries any I/O failure, not only the contended one</b>, and that is a deliberate
    /// trade rather than an oversight: a read-only target and a target that is a directory raise the
    /// same two exception types as a sharing violation, and telling them apart means matching
    /// platform error codes — brittle, and wrong in the direction that matters if the match ever
    /// drifts. The cost of the broad filter is that a permanently-failing write takes the full budget
    /// before surfacing the same exception it would have raised immediately. Paying the budget to
    /// report a broken path is a better bargain than silently not retrying a real one.
    /// </para>
    /// </remarks>
    private static async Task ReplaceWithRetryAsync(
        string staging, string target, TimeSpan replaceRetryBudget, CancellationToken cancellationToken)
    {
        // The first attempt always runs before the deadline is consulted, so a zero budget still tries
        // exactly once — which is what the exhaustion test injects.
        var deadlineTicks = Environment.TickCount64 + (long)replaceRetryBudget.TotalMilliseconds;
        var backoffMs = 15.0;
        while (true)
        {
            try
            {
                File.Move(staging, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadlineTicks)
                {
                    throw;
                }

                // wait-ok: yielding to a reader that closes in microseconds; the ceiling is the budget
                await Task.Delay(TimeSpan.FromMilliseconds(backoffMs), cancellationToken).ConfigureAwait(false);
                backoffMs = Math.Min(backoffMs * 2, MaxReplaceBackoffMs);
            }
        }
    }
}
