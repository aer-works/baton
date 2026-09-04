namespace Baton.Vendors.Tests;

/// <summary>
/// Same purpose as <c>Baton.Cli.Tests.ConsoleErrorCaptureCollection</c>, defined again because xUnit
/// scopes collections per assembly and this is a different one.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
