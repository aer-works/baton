namespace Baton.Cli.Tests;

/// <summary>
/// Serializes every test class in this assembly that swaps the process-global
/// <see cref="Console.Out"/> stream via <see cref="Console.SetOut(System.IO.TextWriter)"/> to capture
/// CLI output. Two SetOut-swapping classes running in parallel interleave — one test's SetOut lands
/// between another's capture and restore, and each reads the other's output — the same #967/#1607
/// race <see cref="ConsoleErrorCaptureCollection"/> closes for <see cref="Console.Error"/> in this
/// assembly. <c>ConsoleSwapTests</c> (#1783) is the build-time guard that a class added later can't
/// swap <see cref="Console.Out"/> without enrolling in this or another <c>DisableParallelization</c>
/// collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutCaptureCollection
{
    public const string Name = "console-out-capture";
}
