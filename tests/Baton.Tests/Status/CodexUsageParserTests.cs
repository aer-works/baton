using Baton.Status;

namespace Baton.Tests.Status;

public sealed class CodexUsageParserTests
{
    [Fact]
    public void Completed_turn_separates_uncached_input_from_cache_and_preserves_other_usage_fields()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":14750,"cached_input_tokens":8960,"cache_write_input_tokens":17,"output_tokens":211,"reasoning_output_tokens":48}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.NotNull(usage);
        Assert.Equal(5790, usage.TokensIn);
        Assert.Equal(8960, usage.CacheReadTokens);
        Assert.Equal(17, usage.CacheCreationTokens);
        Assert.Equal(211, usage.TokensOut);
        Assert.Equal(48, usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }

    [Fact]
    public void Incremental_and_final_parsing_use_the_same_per_turn_semantics()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":19579,"cached_input_tokens":11008,"output_tokens":9,"reasoning_output_tokens":0}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var final));
        Assert.True(parser.TryParseIncrementalUsage(line, out var incremental));
        Assert.Equal(final, incremental);
    }

    [Fact]
    public void Cached_input_larger_than_total_input_clamps_uncached_input_to_zero()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"input_tokens":5,"cached_input_tokens":8}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Equal(0, usage!.TokensIn);
        Assert.Equal(8, usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"turn.started\"}")]
    [InlineData("{\"type\":\"turn.completed\"}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{}}")]
    [InlineData("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":null,\"cached_input_tokens\":null,\"cache_write_input_tokens\":null,\"output_tokens\":null,\"reasoning_output_tokens\":null}}")]
    public void Malformed_unrelated_and_all_null_lines_are_not_usage(string line)
    {
        var parser = new CodexUsageParser();

        Assert.False(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage);
    }

    [Fact]
    public void A_partial_usage_object_preserves_absence_instead_of_fabricating_zeroes()
    {
        var parser = new CodexUsageParser();
        const string line = """
            {"type":"turn.completed","usage":{"cached_input_tokens":321}}
            """;

        Assert.True(parser.TryParseFinalUsage(line, out var usage));
        Assert.Null(usage!.TokensIn);
        Assert.Equal(321, usage.CacheReadTokens);
        Assert.Null(usage.CacheCreationTokens);
        Assert.Null(usage.TokensOut);
        Assert.Null(usage.ThinkingTokens);
        Assert.Equal(1, usage.Turns);
    }
}
