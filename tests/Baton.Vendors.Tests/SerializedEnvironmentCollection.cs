namespace Baton.Vendors.Tests;

/// <summary>
/// See <c>Baton.Cli.Tests.SerializedEnvironmentCollection</c> for the full rationale (#1491); this is
/// this assembly's copy of that same enrollment point. What it protects here specifically: the
/// catalog lookups behind worker roles and workflow templates, plus the Claude config-root path, all
/// go back to their env var on every call instead of caching it, so any class that mutates one of
/// those vars without joining this group is free to bleed into a reader mid-run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerializedEnvironmentCollection
{
    public const string Name = "serialized-environment";
}
