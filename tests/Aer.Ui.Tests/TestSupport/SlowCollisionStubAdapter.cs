using System.Globalization;
using Aer.Adapters;
using Aer.Flow.Dispatch;
using Aer.Flow.Domain;

namespace Aer.Ui.Tests.TestSupport;

/// <summary>
/// #590: detects two dispatches racing the SAME room directory. Each dispatched process checks for
/// a marker file left by another still-running dispatch in the same <see cref="WorkerInvocation.WorkingDirectory"/>,
/// records a collision if one is found, then holds the marker for <see cref="DispatchDelay"/> before
/// clearing it -- long enough that an unserialised pair of concurrent dispatches against one
/// directory is caught deterministically, while two dispatches against two different directories
/// (their own, distinct marker files) never collide by construction.
/// </summary>
internal sealed class SlowCollisionStubAdapter : IWorkerAdapter
{
    public static readonly TimeSpan DispatchDelay = TimeSpan.FromMilliseconds(900);

    public const string MarkerFileName = ".dispatch-marker";
    public const string CollisionFileName = ".dispatch-collision";

    /// <summary>
    /// One line appended by every dispatch that actually reaches this adapter's process -- distinct
    /// from the marker/collision pair above. A caller whose second concurrent dispatch is refused
    /// upstream (e.g. Flow's own per-directory <c>ConcurrencyGuard</c>, spec §15) never reaches this
    /// process at all, so it never collides on the marker either -- which would make "no collision
    /// file" a false pass for a dispatch that was silently dropped rather than one that was safely
    /// serialised. Counting completions is what tells the two apart.
    /// </summary>
    public const string CompletionsFileName = ".dispatch-completions";

    /// <summary>
    /// #1211: embed <c>STUB_RENDEZVOUS_DIR=&lt;path&gt;</c> in the prompt template and this dispatch
    /// announces itself into that shared directory, then waits up to <see cref="RendezvousTimeout"/>
    /// for a second dispatch to announce too. Only a pair that is genuinely in flight at once can
    /// both clear the wait, so the timeout can be generous without weakening the claim -- which is
    /// what the wall-clock start gap it replaces never had.
    /// </summary>
    public const string RendezvousSentinelPrefix = "STUB_RENDEZVOUS_DIR=";

    public const string ArrivalFilePrefix = "arrived-";
    public const string ConcurrencyProofFilePrefix = "concurrent-";

    public static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RendezvousPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Embedded in <see cref="WorkerInvocation.PromptTemplate"/> (same convention as
    /// <c>SessionTurnStubAdapter.FailureSentinel</c>) to force this dispatch to exit non-zero after
    /// still doing its marker/collision/completions bookkeeping -- used to drive a step to a
    /// deterministic Failed/Paused state so a <c>RetryWithRevision</c> decision has something to
    /// legitimately re-dispatch (<c>ExternalDecisionValidator</c> refuses that decision once the
    /// paused outcome is Succeeded).
    /// </summary>
    public const string ForceFailureSentinel = "STUB_FORCE_DISPATCH_FAILURE";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        var outputName = contract.ProducedOutputs.Count > 0
            ? contract.ProducedOutputs[0].Name
            : "out";
        var dir = invocation.WorkingDirectory ?? Path.GetTempPath();
        var markerFile = Path.Combine(dir, MarkerFileName);
        var collisionFile = Path.Combine(dir, CollisionFileName);
        var completionsFile = Path.Combine(dir, CompletionsFileName);
        var shouldFail = invocation.PromptTemplate.Contains(ForceFailureSentinel, StringComparison.Ordinal);
        var rendezvousDir = ReadRendezvousDirectory(invocation.PromptTemplate);
        var pollCount = (int)(RendezvousTimeout.TotalMilliseconds / RendezvousPollInterval.TotalMilliseconds);

        if (OperatingSystem.IsWindows())
        {
            // PowerShell, not cmd: SessionTurnStubAdapter's NoOutputFileSentinel/AgyNoOutputFileSentinel
            // comments already measured that an embedded `>` inside a single combined `cmd /c "..."`
            // argv element silently produces no file at all when several quoted-path redirects are
            // chained with `&` -- exactly this script's original shape. Following the same working
            // pattern ShellWorkerCommands.BlockUntilReleased already uses in this project (Test-Path /
            // New-Item -Force / single-quoted literal paths / $env:AER_OUTPUT_DIR via Join-Path).
            var finalStep = shouldFail
                ? "exit 1"
                : $"Set-Content -Path (Join-Path $env:AER_OUTPUT_DIR '{outputName}') -Value 'stub-response'";
            var rendezvous = rendezvousDir is null
                ? ""
                : $"New-Item -ItemType File -Force (Join-Path '{rendezvousDir}' ('{ArrivalFilePrefix}' + $PID)) | Out-Null; " +
                  $"for ($i = 0; $i -lt {pollCount}; $i++) {{ " +
                  $"if (@(Get-ChildItem -Path '{rendezvousDir}' -Filter '{ArrivalFilePrefix}*').Count -ge 2) {{ " +
                  $"New-Item -ItemType File -Force (Join-Path '{rendezvousDir}' ('{ConcurrencyProofFilePrefix}' + $PID)) | Out-Null; break }}; " +
                  $"Start-Sleep -Milliseconds {RendezvousPollInterval.TotalMilliseconds} }}; ";
            var script =
                $"if (Test-Path '{markerFile}') {{ Add-Content -Path '{collisionFile}' -Value 'collision' }}; " +
                $"New-Item -ItemType File -Force '{markerFile}' | Out-Null; " +
                rendezvous +
                $"Start-Sleep -Milliseconds {DispatchDelay.TotalMilliseconds}; " +
                $"Remove-Item -Force '{markerFile}'; " +
                $"Add-Content -Path '{completionsFile}' -Value 'done'; " +
                finalStep;
            return new CoreDispatchTarget("powershell", ["-NoProfile", "-Command", script]);
        }
        else
        {
            var finalStep = shouldFail ? "exit 1" : $"echo stub-response > \"$AER_OUTPUT_DIR/{outputName}\"";
            var rendezvous = rendezvousDir is null
                ? ""
                : $"touch '{rendezvousDir}/{ArrivalFilePrefix}'$$; " +
                  $"i=0; while [ $i -lt {pollCount} ]; do " +
                  $"arrived=$(ls '{rendezvousDir}' | grep -c '^{ArrivalFilePrefix}'); " +
                  $"if [ \"$arrived\" -ge 2 ]; then touch '{rendezvousDir}/{ConcurrencyProofFilePrefix}'$$; break; fi; " +
                  // Invariant, not current culture: a comma-decimal locale would emit `sleep 0,1`, and
                  // the poll would stop pacing itself rather than fail loudly.
                  $"sleep {RendezvousPollInterval.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture)}; i=$((i+1)); done; ";
            var script =
                $"if [ -f '{markerFile}' ]; then echo collision >> '{collisionFile}'; fi; " +
                $"touch '{markerFile}'; " +
                rendezvous +
                $"sleep 1; " +
                $"rm -f '{markerFile}'; " +
                $"echo done >> '{completionsFile}'; " +
                finalStep;
            return new CoreDispatchTarget("sh", ["-c", script]);
        }
    }

    /// <summary>The path after <see cref="RendezvousSentinelPrefix"/>, or null if the template carries none.</summary>
    private static string? ReadRendezvousDirectory(string promptTemplate)
    {
        var start = promptTemplate.IndexOf(RendezvousSentinelPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var valueStart = start + RendezvousSentinelPrefix.Length;
        var end = promptTemplate.IndexOfAny(['\r', '\n'], valueStart);
        return (end < 0 ? promptTemplate[valueStart..] : promptTemplate[valueStart..end]).Trim();
    }
}
