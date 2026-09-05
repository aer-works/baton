using System.ComponentModel;
using System.Diagnostics;

namespace Baton.Outcomes;

public enum EngineLivenessStatus
{
    Alive,
    Dead,
    Unknown
}

public sealed record EngineLivenessResult(EngineLivenessStatus Status, string? Why = null);

/// <summary>
/// Whether the engine process that recorded a <c>FlowEvent.ExecutionRequestAccepted</c> is still
/// alive — the one liveness mechanism this codebase has, consulted by both <c>baton status</c>'s human
/// rendering (<c>Baton.Cli.StatusCommand.FormatStepStatus</c>) and <c>baton resume</c>'s STALLED
/// reconciliation (<c>MutationInterface.RecordResumeAsync</c>, issue #1359 F3) — never a second,
/// independently-invented check.
/// </summary>
public static class EngineLivenessProbe
{
    public static EngineLivenessResult Probe(int? pid, DateTimeOffset? startTime)
    {
        if (pid is null || startTime is null)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "no process identity recorded");
        }

        if (pid.Value <= 0)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "invalid process identity");
        }

        try
        {
            using var process = Process.GetProcessById(pid.Value);

            DateTimeOffset processStartTime;
            try
            {
                processStartTime = new DateTimeOffset(process.StartTime).ToUniversalTime();
            }
            catch (InvalidOperationException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
            }

            var recordedUtc = startTime.Value.ToUniversalTime();
            var diffMs = Math.Abs((processStartTime - recordedUtc).TotalMilliseconds);
            if (diffMs > 1000)
            {
                return new EngineLivenessResult(EngineLivenessStatus.Dead);
            }

            return ProbeProcess(process);
        }
        catch (ArgumentException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (InvalidOperationException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
        }
    }

    /// <summary>
    /// Checks the existing process liveness probe without a start timestamp. Core lifecycle events
    /// carry a worker PID but not its start time, so this only declares a worker dead when the OS
    /// confirms that PID is absent or exited.
    /// </summary>
    public static EngineLivenessResult Probe(int pid)
    {
        if (pid <= 0)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, "invalid process identity");
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return ProbeProcess(process);
        }
        catch (ArgumentException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (InvalidOperationException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
        }
    }

    private static EngineLivenessResult ProbeProcess(Process process)
    {
        try
        {
            return process.HasExited
                ? new EngineLivenessResult(EngineLivenessStatus.Dead)
                : new EngineLivenessResult(EngineLivenessStatus.Alive);
        }
        catch (InvalidOperationException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Dead);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            return new EngineLivenessResult(EngineLivenessStatus.Unknown, ex.Message);
        }
    }
}