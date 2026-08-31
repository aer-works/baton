namespace Baton.Vendors.Tests;

/// <summary>
/// Serialises <see cref="RoleDispatchTests"/> and <see cref="WorkflowTemplateComposerTests"/>, the two
/// classes that read the <em>shipped</em> worker-role/workflow-template catalogs off the process-global
/// <c>BATON_WORKER_ROLES_PATH</c>/<c>BATON_WORKER_TIERS_PATH</c> env vars without ever repointing them —
/// the original bleed this collection closed was a mutator racing these readers, and the five
/// RoleDispatch assertions that failed against a role named <c>r</c> on a tier <c>t</c> nobody in that
/// test wrote are what it measured. That mutator, <see cref="WorkerRoleCatalogTests"/>, moved out to
/// <c>SerializedEnvironmentCollection</c> (#1491), which keeps it from racing these two just the same;
/// this collection stays because its own two members are non-mutating catalog readers that gain
/// nothing from the assembly's parallel pool and lose nothing by being kept off it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerRoleCatalogCollection
{
    public const string Name = "worker-role-catalog";
}
