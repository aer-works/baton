namespace Baton.Vendors.Tests;

/// <summary>
/// Serialises every test class that resolves an adapter which writes a launch config —
/// <c>claude-settings.json</c> or <c>.agents/hooks.json</c>. The assembly shares one
/// <c>BATON_HOME</c> (<c>tests/Shared/BatonHomeRedirect.cs</c>), so they all resolve to the same files.
/// </summary>
/// <remarks>
/// <para>
/// <b>#667 asked for this to be deleted and the measurement refused.</b> Without it, six runs of this
/// assembly gave five failures across three classes, every one an <c>UnauthorizedAccessException</c>
/// out of <see cref="AtomicLaunchConfigWriter"/>'s <c>File.Move</c> with its attempts spent. #667's
/// skip makes every resolve after the first a no-op, but at assembly start there is no file, so every
/// class racing to its own first resolve is a writer at once — and that budget was exhaustible (fixed
/// by #682, whose remarks carry the mechanism).
/// </para>
/// <para>
/// <b>Left in place after #682.</b> Setting <c>DisableParallelization = false</c> reproduced neither
/// that <c>UnauthorizedAccessException</c> nor any other failure across repeated runs. Loosening it
/// is out of #682's scope rather than ruled out: this attribute may now be doing more than the defect
/// it was added for needs, and that is an opportunity, not something this issue measured against.
/// </para>
/// <para>
/// Seven classes rather than the original two: three of the failures were in classes never covered.
/// <see cref="AgyWorkerAdapterTests"/> is included on the same mechanism rather than its own
/// observed failure — it writes the other launch config through the same writer.
/// <c>ClaudeWorkerAdapterTests</c>, an original member and still a launch-config writer, moved to
/// <see cref="SerializedEnvironmentCollection"/> (#1491, it also mutates env vars); that stays safe
/// against this group because xUnit guarantees a parallelism-opted-out test never runs in parallel
/// against ANY other test — two <c>DisableParallelization</c> collections cannot overlap each other.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LaunchConfigCollection
{
    public const string Name = "launch-config";
}
