namespace Baton.Tests.Projection;

/// <summary>
/// Serializes every test class that touches the process-global <see cref="Console.Error"/> stream —
/// either by swapping it via <see cref="Console.SetError(System.IO.TextWriter)"/> to capture
/// loud-fallback output, or by driving production code that writes to it directly without swapping
/// it. Two SetError-swapping classes running in parallel interleave — one test's SetError lands
/// between another's capture and restore, and each reads the other's output (#967). An unswapped
/// direct writer racing a swap is the same defect from the other side (#1607): its write can land in
/// whichever `TextWriter` happens to be installed at that instant, including another test's capture
/// buffer. Membership alone only serializes members against each other — a non-member class that
/// writes to <see cref="Console.Error"/> directly still races a member's capture (#1778) — so the
/// collection opts out of xUnit's parallel pool entirely, the same way <c>SerializedEnvironmentCollection</c>
/// and the other process-global-state collections do: nothing else in the assembly runs while a member
/// test runs. <c>Baton.Architecture.Tests.ConsoleSwapTests</c> (#1783) is the build-time guard that a
/// class added later can't swap <see cref="Console.Error"/>/<see cref="Console.Out"/> without
/// enrolling in this or another <c>DisableParallelization</c> collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
