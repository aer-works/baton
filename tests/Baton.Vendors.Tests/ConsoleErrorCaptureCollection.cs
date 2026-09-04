namespace Baton.Vendors.Tests;

/// <summary>
/// Serializes every test class in this assembly that swaps the process-global
/// <see cref="Console.Error"/> stream via <see cref="Console.SetError(System.IO.TextWriter)"/>. Two
/// SetError-swapping classes running in parallel interleave — one test's SetError lands between
/// another's capture and restore, and each reads the other's output — the same #967/#1607 race
/// <c>Baton.Tests.Projection.ConsoleErrorCaptureCollection</c> closes for its own assembly.
/// Collections are per-assembly in xUnit, so this is that collection's sibling here rather than a
/// shared reference. <c>ConsoleSwapTests</c> (#1783) is the build-time guard that a class added later
/// can't swap <see cref="Console.Error"/> without enrolling in this or another
/// <c>DisableParallelization</c> collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
