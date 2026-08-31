using Baton.Status;

namespace Baton.Tests.Status;

public class BatonPathsTests
{
    /// <summary>
    /// Pins <see cref="BatonPaths.RoomEvidenceFileNames"/>'s contents and order as a machine-checked
    /// fact rather than prose — decision 0057 rule 4 lists the four in exactly this order, and a
    /// future rule-4 consumer reads this array (PR #1489 review finding). The Architecture.Tests
    /// literal scan guards where the names are declared; this guards what the evidence set contains.
    /// </summary>
    [Fact]
    public void Room_evidence_file_names_carry_the_four_names_in_decision_0057_order()
    {
        Assert.Equal(
            new[] { "room.jsonl", "flow.jsonl", "snapshot.json", "flow.lock" },
            BatonPaths.RoomEvidenceFileNames);
    }
}
