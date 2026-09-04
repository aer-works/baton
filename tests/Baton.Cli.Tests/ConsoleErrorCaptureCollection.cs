namespace Baton.Cli.Tests;

/// <summary>
/// This assembly's counterpart to <c>Baton.Tests.Projection.ConsoleErrorCaptureCollection</c> --
/// xUnit collections are scoped per assembly. See that class for the race it closes and for
/// <c>ConsoleSwapTests</c> (#1783). <see cref="ConsoleOutCaptureCollection"/> is this assembly's
/// sibling for <see cref="Console.Out"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
