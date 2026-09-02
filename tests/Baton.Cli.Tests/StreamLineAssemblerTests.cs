using System.Text;

namespace Baton.Cli.Tests;

/// <summary>
/// Unit coverage for <see cref="StreamLineAssembler"/> (#1574): the shared line-buffering reader
/// both <c>room_detail</c> and <c>baton status --follow</c> route worker stdout/stderr through
/// before rendering. Bytes in at arbitrary chunk boundaries, complete lines out; a partial trailing
/// line is held, never rendered, never duplicated on the next <see cref="StreamLineAssembler.Append"/>.
/// </summary>
public sealed class StreamLineAssemblerTests
{
    [Fact]
    public void LineSplitAcrossTwoPolls_RendersExactlyOnce()
    {
        var assembler = new StreamLineAssembler();

        var firstHalf = assembler.Append(Encoding.UTF8.GetBytes("hello wo"));
        Assert.Empty(firstHalf);

        var secondHalf = assembler.Append(Encoding.UTF8.GetBytes("rld\n"));
        Assert.Equal(["hello world"], secondHalf);

        // Never re-emitted on a later, unrelated append.
        var next = assembler.Append(Encoding.UTF8.GetBytes("next\n"));
        Assert.Equal(["next"], next);
    }

    [Fact]
    public void PollLandingExactlyOnNewline_EmitsTheCompletedLineAndHoldsNothing()
    {
        var assembler = new StreamLineAssembler();

        var lines = assembler.Append(Encoding.UTF8.GetBytes("first line\n"));
        Assert.Equal(["first line"], lines);

        var moreLines = assembler.Append(Encoding.UTF8.GetBytes("second line\n"));
        Assert.Equal(["second line"], moreLines);
    }

    [Fact]
    public void Crlf_StripsTheCarriageReturn()
    {
        var assembler = new StreamLineAssembler();

        var lines = assembler.Append(Encoding.UTF8.GetBytes("windows line\r\nnext\r\n"));

        Assert.Equal(["windows line", "next"], lines);
    }

    [Fact]
    public void FinalUnterminatedLine_IsHeldNotRendered()
    {
        var assembler = new StreamLineAssembler();

        var lines = assembler.Append(Encoding.UTF8.GetBytes("complete\nincomplete tail"));

        Assert.Equal(["complete"], lines);

        // The held tail only renders once it is actually completed by a later append.
        var next = assembler.Append(Encoding.UTF8.GetBytes(" continues\n"));
        Assert.Equal(["incomplete tail continues"], next);
    }

    [Fact]
    public void MultiByteUtf8CharacterSplitAcrossPolls_DecodesCleanly()
    {
        var assembler = new StreamLineAssembler();
        var bytes = Encoding.UTF8.GetBytes("café\n");

        // Split inside the 2-byte 'é' sequence (0xC3 0xA9).
        var splitIndex = bytes.Length - 2;
        var firstChunk = bytes[..(splitIndex + 1)];
        var secondChunk = bytes[(splitIndex + 1)..];

        Assert.Empty(assembler.Append(firstChunk));
        var lines = assembler.Append(secondChunk);

        Assert.Equal(["café"], lines);
    }

    [Fact]
    public void Flush_DrainsAHeldPartialLine_ForACallerThatKnowsNoMoreBytesAreComing()
    {
        var assembler = new StreamLineAssembler();
        assembler.Append(Encoding.UTF8.GetBytes("complete\nno trailing newline"));

        Assert.Equal("no trailing newline", assembler.Flush());

        // Drained, not just peeked: a second Flush (or Append) never re-emits it.
        Assert.Null(assembler.Flush());
    }

    [Fact]
    public void Flush_WithNothingHeld_ReturnsNull()
    {
        var assembler = new StreamLineAssembler();
        assembler.Append(Encoding.UTF8.GetBytes("terminated\n"));

        Assert.Null(assembler.Flush());
    }
}
