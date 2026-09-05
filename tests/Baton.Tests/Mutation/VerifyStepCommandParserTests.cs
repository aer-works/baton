using Baton.Mutation;

namespace Baton.Tests.Mutation;

/// <summary>
/// The parse-time allowlist on <c>--verify-cmd</c> (#1882). The population these pin is the SHAPES,
/// in both directions: an allowed shape parses to the argv that will actually be spawned, and every
/// rejected shape is named individually rather than covered by one "something was rejected" test —
/// a single negative case cannot tell "the allowlist works" apart from "the parser rejects
/// everything", which is the control this file needs.
/// </summary>
public class VerifyStepCommandParserTests
{
    [Theory]
    [InlineData("dotnet build -warnaserror")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --project tests/Baton.Tests --minimum-expected-tests 1")]
    [InlineData("python tools/audit-completeness/audit.py --check")]
    [InlineData("python benchmarks/deepswe/derive_scores.py --check-all")]
    [InlineData("python tools/gates/gates.py --selftest")]
    [InlineData("python tools\\gates\\gates.py --selftest")]
    public void An_allowlisted_shape_parses(string commandLine)
    {
        Assert.True(VerifyStepCommandParser.TryParse(commandLine, out var command, out var error), error);
        Assert.Null(error);
        Assert.Equal(commandLine, command!.CommandLine);
    }

    [Theory]
    // Not one of the three programs at all.
    [InlineData("pixi run gates")]
    [InlineData("git push origin HEAD")]
    [InlineData("claude -p \"review this\"")]
    // Right program, wrong verb -- `dotnet` is not a blanket grant.
    [InlineData("dotnet run --project src/Baton.Cli")]
    [InlineData("dotnet nuget push pkg.nupkg")]
    // A python script outside the two allowed roots, or escaping them, or not a script at all.
    [InlineData("python scripts/whatever.py --check")]
    [InlineData("python tools/../scripts/evil.py --check")]
    [InlineData("python C:/tools/evil.py --check")]
    [InlineData("python tools/gates/gates.py")]
    [InlineData("python tools/gates/gates.py --fix")]
    // An absolute or relative path standing in for the allowlisted program name.
    [InlineData("C:/tools/dotnet.exe build")]
    [InlineData("../dotnet build")]
    // Shell syntax, which nothing here would interpret.
    [InlineData("dotnet build && dotnet test")]
    [InlineData("dotnet build | tee log.txt")]
    [InlineData("dotnet build > out.txt")]
    // Degenerate input.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dotnet")]
    [InlineData("dotnet build \"unclosed")]
    public void A_rejected_shape_is_refused_and_the_offending_command_is_named(string commandLine)
    {
        Assert.False(VerifyStepCommandParser.TryParse(commandLine, out var command, out var error));
        Assert.Null(command);
        Assert.NotNull(error);

        // The refusal quotes the input back (VerifyStepCommandParser's own doc says why). A blank
        // command has no text to quote, and names the flag instead.
        if (commandLine.Trim().Length > 0)
        {
            Assert.Contains(commandLine.Trim(), error);
        }
        else
        {
            Assert.Contains("--verify-cmd", error);
        }
    }

    [Fact]
    public void The_argv_is_tokenized_so_a_quoted_argument_survives_as_one_word()
    {
        Assert.True(VerifyStepCommandParser.TryParse(
            "dotnet test --filter-class \"Baton.Tests.Some Class\"", out var command, out _));

        Assert.Equal(["dotnet", "test", "--filter-class", "Baton.Tests.Some Class"], command!.Argv);
    }

    [Fact]
    public void A_quoted_argument_cannot_smuggle_a_different_program_past_the_shape_check()
    {
        // The shape check runs over the argv, not the raw text: a leading quoted token is the PROGRAM,
        // so this is "run the program named 'dotnet build'", not "run dotnet with the argument build".
        Assert.False(VerifyStepCommandParser.TryParse("\"dotnet build\" -warnaserror", out _, out var error));
        Assert.NotNull(error);
    }
}
