namespace Baton.Mcp.Tests;

/// <summary>
/// Serialises the classes that repoint <c>BATON_HOME</c> for their own per-test temp root
/// (<see cref="FleetStatusToolTests"/>, <see cref="RoomDetailToolTests"/>). xUnit runs different
/// classes' collections in parallel by default, and both of these mutate the same process-global
/// environment variable in their constructor/<see cref="IDisposable.Dispose"/> pair — without this,
/// one class's temp root bleeds into the other mid-run (#1427's own test run first observed it: a
/// room written under <see cref="RoomDetailToolTests"/>'s root read back as "not found" under
/// <see cref="FleetStatusToolTests"/>'s). The <c>Baton.Cli.Tests</c> analogue is
/// <c>WorkerCatalogEnvCollection</c>, created for the same reason.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BatonHomeEnvCollection
{
    public const string Name = "baton-home-env";
}
