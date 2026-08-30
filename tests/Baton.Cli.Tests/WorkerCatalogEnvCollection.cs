namespace Baton.Cli.Tests;

/// <summary>
/// Serialises the classes that repoint the worker-role / workflow-template catalog through its
/// process-global env vars (<c>BATON_WORKER_ROLES_PATH</c>, <c>BATON_WORKER_TIERS_PATH</c>,
/// <c>BATON_WORKFLOW_TEMPLATES_PATH</c>). xUnit runs classes in parallel, and one of them
/// (<see cref="DispatchCommandEndToEndTests"/>) deliberately points the roles path at a <em>malformed</em>
/// catalog to prove a bad catalog is a typed argument error — so without this, that bad path bleeds into
/// <see cref="DispatchTemplateEndToEndTests"/> mid-run and a template dispatch reads it. The
/// <c>Baton.Vendors.Tests</c> analogue is <c>WorkerRoleCatalogCollection</c>, created for the same reason.
/// </summary>
/// <remarks>
/// #929: observed as <c>DispatchTemplateEndToEndTests.A_template_with_a_capture_step_in_a_non_git_workspace_...</c>
/// failing on Windows CI with a raw <c>JsonException</c> ("could not convert to List&lt;RawRole&gt;") from the
/// bled malformed catalog, instead of the <c>CliArgumentException</c> the non-git workspace should have raised.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkerCatalogEnvCollection
{
    public const string Name = "worker-catalog-env";
}
