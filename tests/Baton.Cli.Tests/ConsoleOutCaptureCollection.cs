namespace Baton.Cli.Tests;

/// <summary>
/// <see cref="Console.Out"/> counterpart to <see cref="ConsoleErrorCaptureCollection"/> in this
/// assembly: same race (#967/#1607), same #1783 <c>ConsoleSwapTests</c> enforcement, different
/// stream.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutCaptureCollection
{
    public const string Name = "console-out-capture";
}
