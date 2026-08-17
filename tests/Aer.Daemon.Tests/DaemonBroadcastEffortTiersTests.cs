using System.Text.Json;

namespace Aer.Daemon.Tests;

/// <summary>
/// #1318 (decision 0058's scope ruling 4): <c>WorkerEffortTiers</c> is the additive broadcast sibling
/// carrying the canonical effort word per worker, built from the same bindings-file shape
/// <c>WorkerAdapters</c> already reads. A worker whose bindings entry holds a raw vendor value (or no
/// effort at all) must be absent from the object -- never defaulted, never reverse-mapped -- so a
/// client's absence rule renders no mark rather than fabricating a tier the data does not carry.
/// </summary>
public class DaemonBroadcastEffortTiersTests
{
    [Fact]
    public void Carries_the_canonical_tier_for_a_worker_whose_binding_holds_one()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude", "Effort": "careful" } }
            """);

        var result = DaemonBroadcast.BuildWorkerEffortTiers(doc.RootElement);

        Assert.Equal("careful", result["architect"]!.GetValue<string>());
    }

    [Fact]
    public void Omits_a_worker_whose_binding_holds_a_raw_vendor_value()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude", "Effort": "high" } }
            """);

        var result = DaemonBroadcast.BuildWorkerEffortTiers(doc.RootElement);

        Assert.False(result.ContainsKey("architect"));
    }

    [Fact]
    public void Omits_a_worker_with_no_effort_at_all()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude" } }
            """);

        var result = DaemonBroadcast.BuildWorkerEffortTiers(doc.RootElement);

        Assert.False(result.ContainsKey("architect"));
    }

    [Fact]
    public void Omits_a_worker_whose_effort_is_explicitly_null()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude", "Effort": null } }
            """);

        var result = DaemonBroadcast.BuildWorkerEffortTiers(doc.RootElement);

        Assert.False(result.ContainsKey("architect"));
    }

    [Fact]
    public void Mixes_present_and_absent_entries_in_one_bindings_file()
    {
        using var doc = JsonDocument.Parse("""
            {
              "architect": { "Adapter": "claude", "Effort": "quick" },
              "reviewer": { "Adapter": "agy", "Effort": "high" }
            }
            """);

        var result = DaemonBroadcast.BuildWorkerEffortTiers(doc.RootElement);

        Assert.Equal("quick", result["architect"]!.GetValue<string>());
        Assert.False(result.ContainsKey("reviewer"));
    }
}
