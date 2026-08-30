namespace Baton.Vendors.Tests;

/// <summary>
/// The one-namespace rule (decision 0047 §5): a dispatch name resolves to a role <em>or</em> a template,
/// never both, so <c>baton dispatch &lt;name&gt;</c> is unambiguous. <c>DispatchCommand</c> (in Baton.Cli)
/// refuses a name that is in both catalogs at dispatch time, but that refusal should never fire for a
/// name a user could actually type: this guards the <em>shipped</em> catalogs so a collision is caught
/// as a failing build here, not as a runtime error a user hits. The runtime refusal is the belt; this is
/// the braces.
/// </summary>
[Collection(WorkerRoleCatalogCollection.Name)]
public sealed class CatalogNamespaceTests : IDisposable
{
    private readonly string? _priorRoles = Environment.GetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable);
    private readonly string? _priorTiers = Environment.GetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable);
    private readonly string? _priorTemplates = Environment.GetEnvironmentVariable(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable);

    // Pin the shipped catalogs so this reads what ships, not an operator's local override on this
    // machine. In the serialised catalog collection, so the env edit never bleeds into a parallel reader.
    public CatalogNamespaceTests()
    {
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.RolesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"));
        Environment.SetEnvironmentVariable(
            WorkerRoleCatalog.TiersPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"));
        Environment.SetEnvironmentVariable(
            WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.RolesPathEnvironmentVariable, _priorRoles);
        Environment.SetEnvironmentVariable(WorkerRoleCatalog.TiersPathEnvironmentVariable, _priorTiers);
        Environment.SetEnvironmentVariable(WorkflowTemplateCatalog.TemplatesPathEnvironmentVariable, _priorTemplates);
    }

    [Fact]
    public void No_shipped_template_id_collides_with_a_shipped_role_id()
    {
        var roleIds = WorkerRoleCatalog.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var templateIds = WorkflowTemplateCatalog.All.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        var collisions = roleIds.Intersect(templateIds, StringComparer.Ordinal).ToArray();

        Assert.True(
            collisions.Length == 0,
            $"These names are both a shipped role and a shipped template: {string.Join(", ", collisions)}. "
            + "Dispatch is one namespace (0047 §5) — rename one so 'baton dispatch <name>' stays unambiguous.");
    }
}
