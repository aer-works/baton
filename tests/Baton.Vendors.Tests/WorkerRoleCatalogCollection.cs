namespace Baton.Vendors.Tests;

/// <summary>
/// Serialises every test class that resolves the worker-role catalog. Resolution keys off the
/// process-global <c>BATON_WORKER_ROLES_PATH</c>/<c>BATON_WORKER_TIERS_PATH</c> env vars, which
/// <see cref="WorkerRoleCatalogTests"/> repoints at fixtures mid-test. A class reading the <em>shipped</em>
/// catalog (<see cref="RoleDispatchTests"/>) that ran in parallel would resolve whichever fixture was
/// pointed at just then — the exact bleed that made five RoleDispatch assertions fail against a role
/// named <c>r</c> on a tier <c>t</c> nobody in that test wrote. Same collection ⇒ never concurrent.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerRoleCatalogCollection
{
    public const string Name = "worker-role-catalog";
}
