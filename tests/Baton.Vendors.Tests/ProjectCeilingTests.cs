namespace Baton.Vendors.Tests;

/// <summary>#1166: <see cref="ProjectCeiling.Cap"/>'s intersection rule and <see cref="ProjectCeiling.IsUnrestricted"/>.</summary>
public class ProjectCeilingTests
{
    [Fact]
    public void Cap_narrows_a_category_the_ceiling_withholds()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true);
        var ceiling = new ProjectCeiling(ReadFiles: true, WriteFiles: false, RunShellCommands: true, NetworkAccess: true);

        var capped = ceiling.Cap(grant);

        Assert.True(capped.ReadFiles);
        Assert.False(capped.WriteFiles);
    }

    [Fact]
    public void Cap_never_grants_a_category_the_role_itself_withheld()
    {
        var grant = new PermissionGrant(ReadFiles: false, WriteFiles: true);
        var ceiling = ProjectCeiling.Unrestricted;

        var capped = ceiling.Cap(grant);

        Assert.False(capped.ReadFiles);
    }

    [Fact]
    public void Unrestricted_caps_nothing()
    {
        var grant = new PermissionGrant(ReadFiles: true, WriteFiles: true, RunShellCommands: true, NetworkAccess: true);

        var capped = ProjectCeiling.Unrestricted.Cap(grant);

        Assert.Equal(grant, capped);
    }

    [Fact]
    public void IsUnrestricted_is_true_only_when_every_category_is_open()
    {
        Assert.True(ProjectCeiling.Unrestricted.IsUnrestricted);
        Assert.False(new ProjectCeiling(true, true, true, false).IsUnrestricted);
    }
}
