using Baton.Status;

namespace Baton.Vendors.Tests;

/// <summary>
/// The one-namespace rule (decision 0047 §5): a dispatch name resolves to a role <em>or</em> a template,
/// never both, so <c>baton dispatch &lt;name&gt;</c> is unambiguous. <c>DispatchCommand</c> (in Baton.Cli)
/// refuses a name that is in both catalogs at dispatch time, but that refusal should never fire for a
/// name a user could actually type: this guards the <em>shipped</em> catalogs so a collision is caught
/// as a failing build here, not as a runtime error a user hits. The runtime refusal is the belt; this is
/// the braces.
/// </summary>
/// <remarks>
/// #1524: pins the shipped catalogs via an isolated <see cref="BatonEnvironmentSnapshot.BeginScope"/>,
/// not a process mutation, so this class needs no <c>SerializedEnvironmentCollection</c> enrollment
/// and runs parallel-safe.
/// </remarks>
public sealed class CatalogNamespaceTests : IDisposable
{
    // Pin the shipped catalogs so this reads what ships, not an operator's local override on this
    // machine.
    private readonly IDisposable _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with
    {
        WorkerRolesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerRoles.json"),
        WorkerTiersPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkerTiers.json"),
        WorkflowTemplatesPathOverride = Path.Combine(AppContext.BaseDirectory, "WorkflowTemplates.json"),
    });

    public void Dispose() => _scope.Dispose();

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
