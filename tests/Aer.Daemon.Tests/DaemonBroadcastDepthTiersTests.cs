using System.Text.Json;

namespace Aer.Daemon.Tests;

/// <summary>
/// #1339 (decision 0058's scope ruling 4): pins <see cref="DaemonBroadcast.BuildWorkerDepthTiers"/> --
/// see its own doc comment for what it reads and why -- against the polarity set the issue names: a
/// claude worker on a recorded alias carries a tier; an agy worker carries none; an unrecognised
/// model carries none.
/// </summary>
public class DaemonBroadcastDepthTiersTests
{
    [Fact]
    public void Carries_the_canonical_tier_for_a_claude_worker_on_a_recorded_alias()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude", "Model": "opus" } }
            """);

        var result = DaemonBroadcast.BuildWorkerDepthTiers(doc.RootElement);

        Assert.Equal("deep", result["architect"]!.GetValue<string>());
    }

    [Fact]
    public void Omits_an_agy_worker_entirely()
    {
        using var doc = JsonDocument.Parse("""
            { "reviewer": { "Adapter": "agy", "Model": "gemini-3.6-flash-thinking" } }
            """);

        var result = DaemonBroadcast.BuildWorkerDepthTiers(doc.RootElement);

        Assert.False(result.ContainsKey("reviewer"));
    }

    [Fact]
    public void Omits_a_worker_whose_model_the_table_does_not_carry()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude", "Model": "claude-opus-4-8" } }
            """);

        var result = DaemonBroadcast.BuildWorkerDepthTiers(doc.RootElement);

        Assert.False(result.ContainsKey("architect"));
    }

    [Fact]
    public void Omits_a_worker_with_no_model_at_all()
    {
        using var doc = JsonDocument.Parse("""
            { "architect": { "Adapter": "claude" } }
            """);

        var result = DaemonBroadcast.BuildWorkerDepthTiers(doc.RootElement);

        Assert.False(result.ContainsKey("architect"));
    }

    [Fact]
    public void Mixes_present_and_absent_entries_in_one_bindings_file()
    {
        using var doc = JsonDocument.Parse("""
            {
              "architect": { "Adapter": "claude", "Model": "sonnet" },
              "reviewer": { "Adapter": "agy", "Model": "gpt-oss-120b-medium" }
            }
            """);

        var result = DaemonBroadcast.BuildWorkerDepthTiers(doc.RootElement);

        Assert.Equal("balanced", result["architect"]!.GetValue<string>());
        Assert.False(result.ContainsKey("reviewer"));
    }
}
