using Aer.Ui.Converters;
using Aer.Ui.Core;

namespace Aer.Ui.Tests;

/// <summary>
/// #1219's gate, and it exists because the existing ones did not catch what driving the app did.
/// <para>
/// Adding a status went four places: the token file, both toolkits' hand-drawn marks, and — the one
/// that was missed — the desktop's <see cref="StatusIconMap"/> mapping from the state to those
/// marks. <c>DesignTokenDriftTests.EveryStatusMarkIsDrawnByBothToolkits</c> passed throughout, because
/// it asks whether a mark is *drawable*, not whether any state reaches it.
/// </para>
/// <para>
/// The converters already end in <c>_ =&gt; throw</c> for exactly this (#616). That throw is worth
/// less than it looks: the converters run inside a binding, Avalonia swallows what a converter
/// throws, and the switcher rendered an <em>empty space</em> beside "Stopped" rather than failing.
/// A silent blank is the same failure the mark gate's own doc comment describes. This turns it into
/// a build failure, which is what "never let prose stand in as the enforcement" means here.
/// </para>
/// </summary>
public class StatusMarkMappingTests
{
    public static TheoryData<RoomCardStatus> EveryRoomCardStatus()
    {
        var data = new TheoryData<RoomCardStatus>();
        foreach (var status in Enum.GetValues<RoomCardStatus>())
        {
            data.Add(status);
        }

        return data;
    }

    /// <summary>
    /// Every state maps to a mark and a colour, and both keys resolve to something that actually
    /// exists. Enumerated from the enum rather than listed, so a new member is covered the moment it
    /// is declared — a maintained list would have been added in the same commit that forgot the
    /// mapping, by the same person, for the same reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRoomCardStatus))]
    public void Every_room_card_status_maps_to_a_mark_and_a_colour_that_exist(RoomCardStatus status)
    {
        var repositoryRoot = FindRepositoryRoot();
        var icons = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Aer.Ui", "Theme", "Icons.axaml"));
        var generatedTokens = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Aer.Ui", "Theme", "GeneratedTokens.axaml"));

        var geometryKey = StatusIconMap.GeometryKeyFor(status);
        var colorKey = StatusIconMap.ColorKeyFor(status);

        Assert.Contains($"x:Key=\"{geometryKey}\"", icons);
        Assert.Contains(colorKey, generatedTokens);
    }

    /// <summary>
    /// The control arm. Without it the fact above passes against a converter that answers the same
    /// key for everything — which is precisely the "silently render as some other state" defect #616
    /// added the throw for, and is not the same defect as the missing mapping this class was written
    /// after. Distinct states must reach distinct marks.
    /// </summary>
    [Fact]
    public void Distinct_states_do_not_share_one_mark()
    {
        var marks = Enum.GetValues<RoomCardStatus>()
            .ToDictionary(status => status, StatusIconMap.GeometryKeyFor);

        var collisions = marks
            .GroupBy(entry => entry.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(entry => entry.Key))}")
            .ToList();

        Assert.Empty(collisions);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "design", "tokens.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
