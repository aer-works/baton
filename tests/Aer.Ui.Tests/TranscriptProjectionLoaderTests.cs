using System.Text;
using System.Text.Json;

namespace Aer.Ui.Tests;

/// <summary>
/// Unit-level coverage for <see cref="TranscriptProjectionLoader"/> (M18 Phase 1, #177), against
/// real temp directories like <see cref="ArtifactLineageProjectorTests"/> — the loader's whole job
/// is reading the durable artifact directory (UI spec §10.1). Fixture lines are written by hand to
/// the spec's reader contract, deliberately not via a real worker's writer: the contract under test
/// is the spec's, and producer/consumer agreement end to end was Phase 3's gate (#179, against the
/// dialogue worker before it was archived, #1408), not this class's concern.
/// </summary>
public class TranscriptProjectionLoaderTests
{
    private static string NewOutputDirectory()
        => Path.Combine(Path.GetTempPath(), $"ui-transcript-{Guid.NewGuid():N}");

    private static string TurnLine(int sequence, string role = "initiator", string vendor = "claude",
        string prompt = "prompt", string text = "text")
        => JsonSerializer.Serialize(new { Sequence = sequence, Role = role, Vendor = vendor, Prompt = prompt, Text = text });

    private static string WriteTranscript(string outputDirectory, params string[] lines)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, TranscriptProjectionLoader.TranscriptFileName);
        File.WriteAllText(path, string.Concat(lines.Select(line => line + "\n")));
        return path;
    }

    [Fact]
    public void A_missing_output_directory_projects_null_not_an_error()
    {
        Assert.Null(TranscriptProjectionLoader.Load(NewOutputDirectory()));
    }

    [Fact]
    public void An_output_directory_without_a_transcript_projects_null()
    {
        var outputDirectory = NewOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "final.md"), "declared output");

        Assert.Null(TranscriptProjectionLoader.Load(outputDirectory));
    }

    [Fact]
    public void A_complete_transcript_projects_every_turn_in_file_order()
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory,
            TurnLine(1, role: "initiator", vendor: "claude", prompt: "seed", text: "opening"),
            TurnLine(2, role: "responder", vendor: "gemini", prompt: "opening", text: "reply"));

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Collection(projection.Lines,
            line => Assert.Equal(new TranscriptLine.Turn(1, "initiator", "claude", "seed", "opening"), line),
            line => Assert.Equal(new TranscriptLine.Turn(2, "responder", "gemini", "opening", "reply"), line));
    }

    [Fact]
    public void File_order_is_projection_order_even_when_sequence_numbers_disagree()
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory, TurnLine(2), TurnLine(1));

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Equal([2, 1], projection.Lines.Cast<TranscriptLine.Turn>().Select(turn => turn.Sequence));
    }

    [Fact]
    public void An_empty_transcript_projects_zero_lines_not_null()
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory);

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Empty(projection.Lines);
    }

    [Fact]
    public void A_torn_final_line_projects_as_a_malformed_marker_after_the_intact_turns()
    {
        var outputDirectory = NewOutputDirectory();
        var path = WriteTranscript(outputDirectory, TurnLine(1), TurnLine(2));
        File.AppendAllText(path, "{\"Sequence\":3,\"Role\":\"initia");

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Collection(projection.Lines,
            line => Assert.Equal(1, Assert.IsType<TranscriptLine.Turn>(line).Sequence),
            line => Assert.Equal(2, Assert.IsType<TranscriptLine.Turn>(line).Sequence),
            line => Assert.Equal(new TranscriptLine.Malformed(3), line));
    }

    [Fact]
    public void A_malformed_middle_line_is_marked_in_place_and_later_turns_still_project()
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory, TurnLine(1), "not json at all", TurnLine(3));

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Collection(projection.Lines,
            line => Assert.IsType<TranscriptLine.Turn>(line),
            line => Assert.Equal(new TranscriptLine.Malformed(2), line),
            line => Assert.Equal(3, Assert.IsType<TranscriptLine.Turn>(line).Sequence));
    }

    [Theory]
    [InlineData("{\"Role\":\"r\",\"Vendor\":\"v\",\"Prompt\":\"p\",\"Text\":\"t\"}")] // no Sequence
    [InlineData("{\"Sequence\":\"1\",\"Role\":\"r\",\"Vendor\":\"v\",\"Prompt\":\"p\",\"Text\":\"t\"}")] // Sequence not a number
    [InlineData("{\"Sequence\":1,\"Vendor\":\"v\",\"Prompt\":\"p\",\"Text\":\"t\"}")] // no Role
    [InlineData("{\"Sequence\":1,\"Role\":null,\"Vendor\":\"v\",\"Prompt\":\"p\",\"Text\":\"t\"}")] // Role not a string
    [InlineData("{\"Sequence\":1,\"Role\":\"r\",\"Vendor\":\"v\",\"Text\":\"t\"}")] // no Prompt
    [InlineData("{\"Sequence\":1,\"Role\":\"r\",\"Vendor\":\"v\",\"Prompt\":\"p\"}")] // no Text
    [InlineData("[1,2,3]")] // valid JSON, not an object
    public void A_line_missing_or_mistyping_a_required_field_projects_as_malformed(string line)
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory, line);

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Equal(new TranscriptLine.Malformed(1), Assert.Single(projection.Lines));
    }

    [Fact]
    public void Extra_fields_beyond_the_contract_do_not_make_a_turn_malformed()
    {
        var outputDirectory = NewOutputDirectory();
        WriteTranscript(outputDirectory,
            "{\"Sequence\":1,\"Role\":\"r\",\"Vendor\":\"v\",\"Prompt\":\"p\",\"Text\":\"t\",\"Elapsed\":\"3s\"}");

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Equal(new TranscriptLine.Turn(1, "r", "v", "p", "t"), Assert.Single(projection.Lines));
    }

    [Fact]
    public void Loading_succeeds_while_a_writer_still_holds_the_file_open_for_append()
    {
        var outputDirectory = NewOutputDirectory();
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, TranscriptProjectionLoader.TranscriptFileName);

        // The producing worker's exact hold: FileMode.Append + FileShare.Read for the whole
        // exchange. A load-on-refresh mid-run must read what is durably there so far.
        using var writerHold = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes(TurnLine(1) + "\n");
        writerHold.Write(bytes);
        writerHold.Flush();

        var projection = TranscriptProjectionLoader.Load(outputDirectory);

        Assert.NotNull(projection);
        Assert.Equal(1, Assert.IsType<TranscriptLine.Turn>(Assert.Single(projection.Lines)).Sequence);
    }
}
