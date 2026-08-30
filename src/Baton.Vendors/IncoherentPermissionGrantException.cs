using Baton.Flow;

namespace Baton.Vendors;

/// <summary>
/// Raised by <see cref="WorkerBindingResolver.Resolve"/> when an entry's
/// <see cref="PermissionGrant"/> grants the shell while withholding a category the shell reaches
/// (#529). <see cref="PermissionGrant"/> models four independent categories, but a granted shell is
/// a superset of three of them: a worker that can run arbitrary commands can read, write, and reach
/// the network no matter what the other three flags say. Both enforcement points AER has —
/// <c>ClaudeWorkerAdapter.BuildDisallowedTools</c> and the <c>PreToolUse</c> hook check — decide by
/// tool *name*, so neither can tell <c>Bash("cat x")</c> from <c>Read("x")</c>. (The hook also reads
/// a write's target path since #649, but only to exempt the outbox; it still cannot see inside a
/// shell command, which is the gap this refusal exists for.)
///
/// The refusal is at bind time rather than in <see cref="PermissionGrant"/>'s own constructor
/// deliberately: the record is also the adapters' translation input, and their tests legitimately
/// construct one-category grants to assert the flag → tool-name mapping. Coherence is a property of
/// a *configured worker*, which is what this resolver produces.
///
/// This refuses; it does not ask. Decision 0004's "always narrowing, never widening" rules out
/// resolving the contradiction by silently granting the withheld category. Once the interactive
/// permission path lands (#445, #497) this becomes the natural place to raise the question with the
/// operator instead — the categories named here are exactly what such a prompt would have to list.
/// </summary>
public sealed class IncoherentPermissionGrantException : BatonFlowException
{
    public string WorkerName { get; }

    /// <summary>The withheld categories a granted shell would have reached anyway.</summary>
    public IReadOnlyList<string> WithheldCategories { get; }

    public IncoherentPermissionGrantException(string workerName, IReadOnlyList<string> withheldCategories)
        : base(
            $"Worker-binding config entry for '{workerName}' grants RunShellCommands while withholding " +
            $"{string.Join(", ", withheldCategories)}. A worker that can run shell commands can do all " +
            "of those through the shell, so withholding them does not withhold them — AER enforces by " +
            "tool name and cannot tell Bash(\"cat x\") from Read(\"x\") (#529). Either grant the " +
            "withheld categories, making the real reach explicit, or withhold RunShellCommands.")
    {
        WorkerName = workerName;
        WithheldCategories = withheldCategories;
    }
}
