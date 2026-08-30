using Baton.Flow;

namespace Baton.Cli;

/// <summary>
/// Raised for a malformed <c>baton</c> invocation itself — an unknown or missing command-line
/// option, or a missing required argument — before any workflow or bindings file is even read.
/// Mirrors every other config-shaped domain error in the repo (CLAUDE.md's error-handling rules):
/// never a bare <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class CliArgumentException : BatonFlowException
{
    public CliArgumentException(string message)
        : base(message)
    {
    }

    public CliArgumentException(string message, string tryInvocation)
        : base(message)
    {
        TryInvocation = tryInvocation;
    }
}
