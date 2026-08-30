using Aer.VendorProbe;

namespace Baton.Architecture.Tests;

/// <summary>
/// #647: a <c>--vendor</c>-narrowed probe run must not drop the vendor it did not look at.
/// </summary>
/// <remarks>
/// The merge is pure — no CLI, no subscription usage — which is why it is asserted here rather than
/// left to the operator noticing a short matrix after a paid run.
/// </remarks>
public class ProbeMergeTests
{
    private static Finding Row(string vendor, string capability, string version) =>
        new(capability, vendor, Evidence.Inspected, "--flag", ["--help"], "read from help", version);

    [Fact]
    public void A_vendor_this_run_did_not_probe_keeps_its_rows()
    {
        Finding[] previous = [Row("claude", "effort", "2.1.220"), Row("agy", "effort", "1.1.7")];
        Finding[] fresh = [Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry(previous, fresh, ["agy"]);

        Assert.Contains(merged, f => f.Vendor == "claude" && f.VendorVersion == "2.1.220");
    }

    [Fact]
    public void The_probed_vendors_stale_rows_are_replaced_not_duplicated()
    {
        Finding[] previous = [Row("claude", "effort", "2.1.220"), Row("agy", "effort", "1.1.7")];
        Finding[] fresh = [Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry(previous, fresh, ["agy"]);

        var agy = Assert.Single(merged, f => f.Vendor == "agy");
        Assert.Equal("1.1.8", agy.VendorVersion);
    }

    [Fact]
    public void A_capability_the_probe_no_longer_emits_is_not_resurrected_from_the_old_file()
    {
        // Wholesale replacement per vendor, not row-by-row. A row-merging implementation passes both
        // tests above and fails this one — it would keep republishing a finding that nothing
        // currently establishes, which is worse than a missing row because it looks measured.
        Finding[] previous = [Row("agy", "effort", "1.1.7"), Row("agy", "retired-probe", "1.1.7")];
        Finding[] fresh = [Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry(previous, fresh, ["agy"]);

        Assert.DoesNotContain(merged, f => f.Capability == "retired-probe");
    }

    [Fact]
    public void A_full_run_carries_nothing_forward()
    {
        // The control. Without it every assertion above passes on a Carry that returns
        // `previous.Concat(fresh)` unconditionally, which would duplicate every row on a full run.
        Finding[] previous = [Row("claude", "effort", "2.1.219"), Row("agy", "effort", "1.1.7")];
        Finding[] fresh = [Row("claude", "effort", "2.1.220"), Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry(previous, fresh, ["claude", "agy"]);

        Assert.Equal(fresh, merged);
    }

    [Fact]
    public void With_no_prior_file_the_run_stands_alone()
    {
        Finding[] fresh = [Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry([], fresh, ["agy"]);

        Assert.Equal(fresh, merged);
    }

    [Fact]
    public void Carried_rows_come_first_so_a_re_probe_does_not_reorder_the_matrix()
    {
        // The matrix takes its column order from the order vendors first appear. If a narrowed
        // re-probe of agy moved agy into the first column, every diff against the published doc
        // would read as a rewrite.
        Finding[] previous = [Row("claude", "effort", "2.1.220"), Row("agy", "effort", "1.1.7")];
        Finding[] fresh = [Row("agy", "effort", "1.1.8")];

        var merged = ProbeMerge.Carry(previous, fresh, ["agy"]);

        Assert.Equal("claude", merged[0].Vendor);
    }
}
