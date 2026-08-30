using System.Text;
using System.Text.Json;
using Baton.Domain;

namespace Baton.Tests.Domain;

/// <summary>
/// The parse floor of the <c>Diff</c> schema (#881): what must be present, what empty
/// inputs mean, and what bad shapes are refused.
/// </summary>
public class UnifiedDiffSchemaTests
{
    [Fact]
    public void A_real_multi_hunk_diff_parses_as_valid()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            --- a/src/File.cs
            +++ b/src/File.cs
            @@ -1,3 +1,3 @@
            -old line
            +new line
             context
            @@ -10,2 +10,2 @@
            -another old line
            +another new line
            """);

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.NotNull(diff);
        Assert.Contains("--- a/src/File.cs", diff);
    }

    /// <summary>
    /// A deleted line is written with a leading <c>-</c>, so removing a comment that itself starts
    /// <c>-- </c> produces the body line <c>--- note</c> — indistinguishable from a file header on
    /// its own. Matching single lines rejected this valid diff at its SECOND hunk; the pair rule
    /// (see <see cref="UnifiedDiffSchema"/>) is what discriminates.
    /// </summary>
    [Fact]
    public void A_deleted_comment_line_that_looks_like_a_file_header_does_not_break_a_later_hunk()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """
            --- a/schema.sql
            +++ b/schema.sql
            @@ -1,2 +1,2 @@
            --- the old note
            +-- the new note
             select 1;
            @@ -10,2 +10,2 @@
            -select 2;
            +select 3;
            """);

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out _, out var error), error);
        Assert.Null(error);
    }

    /// <summary>
    /// The shapes real <c>git diff</c> output carries that the floor must NOT refuse. Each was
    /// verified by reading once; pinned here because they are what a future edit to the loop would
    /// break first, and reading is not a regression test.
    /// </summary>
    [Theory]
    [InlineData("diff --git a/x b/x\nindex 111..222 100644\n--- a/x\n+++ b/x\n@@ -1,1 +1,1 @@\n-a\n+b\n", "git preamble lines")]
    [InlineData("--- /dev/null\n+++ b/new.cs\n@@ -0,0 +1,1 @@\n+first line\n", "added file against /dev/null")]
    [InlineData("--- a/x\r\n+++ b/x\r\n@@ -1,1 +1,1 @@\r\n-a\r\n+b\r\n", "CRLF line endings")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@ SomeMethod()\n-a\n+b\n context\n", "hunk header with a section heading")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1,1 +1,1 @@\n-a\n+b\n\\ No newline at end of file\n", "no-newline marker")]
    public void Real_git_output_shapes_are_accepted(string diffText, string shape)
    {
        Assert.True(
            UnifiedDiffSchema.TryParse(Encoding.UTF8.GetBytes(diffText), out _, out var error),
            $"The floor refused {shape}: {error}");
    }

    /// <summary>
    /// The deliberate refusal, with its reason: a hunk-less diff (a pure rename or mode change) is
    /// out of the floor by design — <see cref="UnifiedDiffSchema"/>'s comment says why — and the
    /// sentence has to tell the worker what to do instead, since the worker is who reads it.
    /// </summary>
    [Fact]
    public void A_rename_only_diff_is_refused_and_the_sentence_names_the_way_out()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "diff --git a/old.cs b/new.cs\nsimilarity index 100%\nrename from old.cs\nrename to new.cs\n");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("empty file", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The BOM case <see cref="UnifiedDiffSchema"/> records — its comment says why the strip exists.
    /// </summary>
    [Fact]
    public void A_diff_written_with_a_utf8_bom_still_parses()
    {
        var diffText =
            """
            --- a/src/File.cs
            +++ b/src/File.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(diffText)).ToArray();

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out _, out var error), error);
        Assert.Null(error);
    }

    [Fact]
    public void An_empty_file_parses_as_valid_meaning_no_change_proposed()
    {
        var bytes = Array.Empty<byte>();

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.Equal("", diff);
    }

    [Fact]
    public void A_whitespace_only_file_parses_as_valid()
    {
        var bytes = Encoding.UTF8.GetBytes("   \n\t  \r\n ");

        Assert.True(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(error);
        Assert.NotNull(diff);
    }

    [Fact]
    public void Prose_mentioning_hunk_header_without_file_headers_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("Prose description @@ -1,3 +1,3 @@ without headers");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("No hunk header", error);
    }

    [Fact]
    public void Hunk_header_without_preceding_file_header_pair_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("@@ -1,3 +1,3 @@\n-old\n+new");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("without a preceding '--- '/'+++ ' file-header pair", error);
    }

    [Fact]
    public void A_diff_with_headers_but_no_hunk_is_refused_with_a_sentence()
    {
        var bytes = Encoding.UTF8.GetBytes("--- a/src/File.cs\n+++ b/src/File.cs\n");

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("No hunk header", error);
    }

    [Fact]
    public void Invalid_utf8_bytes_are_refused_without_throwing()
    {
        var bytes = new byte[] { 0xFF, 0xFE, 0xFD };

        Assert.False(UnifiedDiffSchema.TryParse(bytes, out var diff, out var error));
        Assert.Null(diff);
        Assert.NotNull(error);
        Assert.Contains("not valid UTF-8 text", error);
    }

    [Fact]
    public void ProducedOutput_serializes_Diff_Schema_as_a_string_and_deserializes()
    {
        var serialized = JsonSerializer.Serialize(new ProducedOutput("patch.diff", Schema: OutputSchema.Diff));
        Assert.Contains("\"Diff\"", serialized);

        var roundTripped = JsonSerializer.Deserialize<ProducedOutput>(serialized);
        Assert.Equal(OutputSchema.Diff, roundTripped!.Schema);

        var caseInsensitive = JsonSerializer.Deserialize<ProducedOutput>(
            """{"Name": "patch.diff", "Schema": "diff"}""");
        Assert.Equal(OutputSchema.Diff, caseInsensitive!.Schema);
    }
}
