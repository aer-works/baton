using System.Reflection;

namespace Baton.Cli.Tests;

/// <summary>
/// #668: what a relative <c>--room-dir</c> cost, and why it is resolved here rather than anywhere
/// downstream, is recorded on <see cref="RoomDirectoryPath"/>.
/// </summary>
/// <remarks>
/// The population is discovered rather than listed. Four entry points take a room directory today
/// and a fifth is the way this regresses: it would be correct everywhere it was remembered and
/// silent where it was not, which is the shape of the original defect. <see cref="EveryParser"/>
/// fails until a new one is covered here.
/// </remarks>
[Collection(WorkingDirectoryCollection.Name)]
public class RoomDirectoryIsResolvedAtTheBoundaryTests
{
    private const string Relative = "task2";

    /// <summary>Each parser, driven through its own argument shape, keyed by the type it lives on.</summary>
    private static readonly Dictionary<Type, Func<string>> Covered = new()
    {
        [typeof(RunOptionsParser)] = () =>
            RunOptionsParser.Parse(["workflow.json", "--bindings", "b.json", "--room-dir", Relative])
                .RoomDirectoryPath,
        [typeof(DispatchOptionsParser)] = () =>
            DispatchOptionsParser.Parse(["review", "--spec", "s.md", "--room-dir", Relative])
                .RoomDirectoryPath,
        [typeof(CancelOptionsParser)] = () =>
            CancelOptionsParser.Parse([Relative, "--execution", "e1", "--bindings", "b.json"])
                .RoomDirectoryPath,
        [typeof(DecideOptionsParser)] = () =>
            DecideOptionsParser.Parse([Relative, "--execution", "e1", "--type", "resume", "--bindings", "b.json"])
                .RoomDirectoryPath,
        [typeof(ResolveOptionsParser)] = () =>
            ResolveOptionsParser.Parse([Relative, "--execution", "e1", "--accept-capture"])
                .RoomDirectoryPath,
        [typeof(SupplyOptionsParser)] = () =>
            SupplyOptionsParser.Parse([Relative, "--worker", "w", "--output", "o", "--file", "f.txt", "--bindings", "b.json"])
                .RoomDirectoryPath,
        [typeof(ResumeOptionsParser)] = () =>
            ResumeOptionsParser.Parse([Relative, "--worker", "w", "--message", "m", "--bindings", "b.json"])
                .RoomDirectoryPath,
        // Redispatch has no --room-dir flag at all (its RoomDirectoryPath is always a fresh generated
        // one, never operator-supplied — spec/baton.md §2). The operator-supplied path this parser
        // actually takes is the positional parent room directory, so that is what must resolve here.
        [typeof(RedispatchOptionsParser)] = () =>
            RedispatchOptionsParser.Parse([Relative]).ParentRoomDirectoryPath,
        [typeof(StatusOptionsParser)] = () =>
            StatusOptionsParser.Parse([Relative]).RoomDirectoryPath,
        [typeof(KeepOptionsParser)] = () =>
            KeepOptionsParser.Parse([Relative]).RoomDirectoryPath,
        [typeof(UnkeepOptionsParser)] = () =>
            UnkeepOptionsParser.Parse([Relative]).RoomDirectoryPath,
        [typeof(RoomDeleteOptionsParser)] = () =>
            RoomDeleteOptionsParser.Parse([Relative]).RoomDirectoryPath,
        [typeof(DeliverOptionsParser)] = () =>
            DeliverOptionsParser.Parse(["file.md", "--room", Relative]).RoomDirectoryPath,
        // WatchOptions.RoomDirectoryPath is nullable (null for --list/--clear-fired), but the
        // Register shape driven here always resolves it -- see WatchOptionsParser.
        [typeof(WatchOptionsParser)] = () =>
            WatchOptionsParser.Parse([Relative, "--notify", "echo hi"]).RoomDirectoryPath!,
    };

    [Fact]
    public void EveryParser_taking_a_room_directory_resolves_a_relative_one()
    {
        foreach (var (parser, parse) in Covered)
        {
            var resolved = parse();

            Assert.True(
                Path.IsPathRooted(resolved),
                $"{parser.Name} returned '{resolved}' for --room-dir '{Relative}'. A worker resolves " +
                "that against its own working directory, not the CLI's, and writes its output where " +
                "AER does not look.");

            // The control. Without it this passes on a parser that returns any absolute path at all
            // — including one that discarded the operator's argument and substituted a default.
            Assert.Equal(Path.GetFullPath(Relative), resolved);
        }
    }

    [Fact]
    public void EveryParser_is_covered_by_the_test_above()
    {
        var takesOne = typeof(RunOptionsParser).Assembly
            .GetTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("OptionsParser", StringComparison.Ordinal))
            .Where(t => t.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
                         ?.ReturnType.GetProperty("RoomDirectoryPath") is not null)
            .ToList();

        Assert.NotEmpty(takesOne);
        Assert.Empty(takesOne.Except(Covered.Keys).Select(t => t.Name));
    }

    [Fact]
    public void BuildEnvironment_refuses_a_relative_path_rather_than_handing_it_to_a_worker()
    {
        // Defence in depth, and the half the CLI fix cannot supply: resolving at the boundary is
        // structural, so any other caller — the daemon, a future entry point, a test fixture —
        // reproduces the silent failure exactly. Loud here, because the cost of being wrong is a
        // whole frontier-model run and a reason nothing names.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "baton-668"));
        var output = Path.Combine(root, "execution_1");

        var relative = Assert.Throws<ArgumentException>(
            () => Baton.Artifacts.ArtifactManager.BuildEnvironment([], "task2/artifacts/execution_1", root));
        Assert.Contains("resolved by the worker process", relative.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(
            () => Baton.Artifacts.ArtifactManager.BuildEnvironment([], output, "task2/artifacts"));

        // The drive-relative arm, and the reason the predicate is IsPathFullyQualified rather than
        // IsPathRooted: on Windows `IsPathRooted("C:task2")` is true while GetFullPath resolves it
        // against the current directory — the very defect, passing the weaker check.
        Assert.Throws<ArgumentException>(
            () => Baton.Artifacts.ArtifactManager.BuildEnvironment([], "C:task2", root));

        // The control: the same call, both fully qualified, must still build — or the guard has
        // disabled the method and every assertion above it means nothing.
        Assert.NotEmpty(Baton.Artifacts.ArtifactManager.BuildEnvironment([], output, root));
    }

    [Fact]
    public void An_absolute_room_directory_is_left_alone()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "baton-668", "task");

        Assert.Equal(
            Path.GetFullPath(absolute),
            RunOptionsParser.Parse(["workflow.json", "--bindings", "b.json", "--room-dir", absolute])
                .RoomDirectoryPath);
    }
}
