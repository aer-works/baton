using Baton.Status;

namespace Baton.Vendors.Tests;

public sealed class CodexRegistrationTests
{
    [Theory]
    [InlineData("quick", "low")]
    [InlineData("standard", "medium")]
    [InlineData("careful", "high")]
    [InlineData("exhaustive", "max")]
    public void Canonical_effort_maps_to_codex_reasoning_effort(string canonical, string expected)
    {
        Assert.Equal(expected, EffortTierMapping.ResolveForCodex(canonical));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    [InlineData("ultra")]
    public void Codex_raw_effort_values_pass_through(string raw)
    {
        Assert.Equal(raw, EffortTierMapping.ResolveForCodex(raw));
    }

    [Fact]
    public void Codex_rejects_an_unknown_effort_instead_of_forwarding_it()
    {
        var exception = Assert.Throws<IncoherentVendorEffortException>(
            () => EffortTierMapping.ResolveForCodex("turbo"));

        Assert.Equal("codex", exception.AdapterName);
        Assert.Contains("turbo", exception.Message);
    }

    [Theory]
    [InlineData("gpt-6-astra", "deep")]
    [InlineData("gpt-5.6-sol", "deep")]
    [InlineData("gpt-5.6-terra", "balanced")]
    [InlineData("gpt-5.6-luna", "fast")]
    public void Registered_codex_models_resolve_to_their_canonical_depth(string model, string expected)
    {
        Assert.True(DepthTierMapping.TryResolve("codex", model, out var depth));
        Assert.Equal(expected, depth);
    }

    [Fact]
    public void Unknown_codex_model_has_no_guessed_depth()
    {
        Assert.False(DepthTierMapping.TryResolve("codex", "gpt-future", out var depth));
        Assert.Equal(string.Empty, depth);
    }

    [Fact]
    public void Codex_is_registered_consistently_as_adapter_usage_parser_and_budgeted_capability()
    {
        Assert.True(WorkerAdapterRegistry.Default.TryGetValue("codex", out var adapter));
        Assert.Equal("CodexWorkerAdapter", adapter.GetType().Name);
        Assert.IsType<CodexUsageParser>(StandardWorkerUsageParsers.Default["codex"]);
        Assert.Contains("codex", WorkerRoleCatalog.KnownTokenBudgetAdapters);
    }
}
