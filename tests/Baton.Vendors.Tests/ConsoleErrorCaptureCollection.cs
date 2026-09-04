namespace Baton.Vendors.Tests;

/// <summary>
/// This assembly's counterpart to <c>Baton.Tests.Projection.ConsoleErrorCaptureCollection</c> --
/// xUnit collections are scoped per assembly, so the guard needs its own definition here too. See
/// that class for the race it closes and for <c>ConsoleSwapTests</c> (#1783), the build-time check
/// enforcing enrollment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
