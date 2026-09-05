using System.Text;
using Baton.Core;

namespace Baton.Vendors;

/// <summary>
/// The half of an <see cref="IVendorUsageSource"/> harvest that is identical for every vendor: wire
/// stdout capture and the <see cref="BatonTaskEventKind.Exited"/> event onto an already-constructed
/// <see cref="BatonTask"/>, run it, and enforce
/// <see cref="IVendorUsageSource.ReadAsync"/>'s "null rather than a fabricated snapshot" contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the task is a parameter rather than built here.</b> Constructing a <see cref="BatonTask"/>
/// is a spawn site, and every spawn site in <c>src/</c> is enumerated by
/// <c>tests/Baton.Architecture.Tests/VendorSpawnGateTests.cs</c>'s <c>ApprovedSpawnSites</c> with the
/// reason it is safe. Constructing the task here would move both vendors' spawn sites into one
/// unreviewed file; taking a constructed task leaves each source named on that list, where its own
/// rationale is recorded.
/// </para>
/// <para>
/// <b>Three ways a harvest yields nothing</b> (#1869 review): the CLI could not be spawned, timed out
/// or was cancelled (a <see cref="BatonException"/>); it ran but exited non-zero; or it exited zero
/// having written nothing at all. Before #1869 only the first was handled, so an errored-but-spawned
/// run parsed <c>""</c> into a snapshot with zero windows and
/// <c>VendorUsageHarvester.Persist</c> atomically overwrote the last good snapshot with it. Each case
/// logs exactly one stderr line and returns null, which the harvester turns into "leave the persisted
/// file alone".
/// </para>
/// <para>
/// <b>Not the same as unrecognizable output.</b> A CLI that exits zero and prints something this
/// vendor's parser does not recognize still produces a snapshot with an empty
/// <see cref="VendorUsageSnapshot.Windows"/> — that is the "harvested, nothing parsed" case
/// <see cref="IVendorUsageSource.ReadAsync"/>'s doc comment keeps distinguishable from "did not
/// harvest at all". Stdout that is empty OR nothing but whitespace is treated as no harvest: a CLI
/// that reports its error on stderr and leaves a bare newline behind on stdout still exercises the
/// overwrite path, since every parser here skips blank lines and would hand back a zero-window
/// snapshot for it.
/// </para>
/// </remarks>
internal static class VendorUsageCommandRun
{
    /// <summary>
    /// Runs <paramref name="task"/> to completion and returns its captured stdout, or null when the
    /// run produced no usable output (see this type's own remarks for the three cases).
    /// </summary>
    internal static async Task<string?> CaptureStdoutOrNullAsync(
        BatonTask task,
        string vendor,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        int? exitCode = null;

        task.EventRaised += (_, e) =>
        {
            switch (e.Kind)
            {
                case BatonTaskEventKind.StdoutChunk when e.Data is { } data:
                    output.Append(Encoding.UTF8.GetString(data));
                    break;
                case BatonTaskEventKind.Exited:
                    exitCode = e.ExitCode;
                    break;
                default:
                    break;
            }
        };

        try
        {
            // BatonProcessRunner joins both drain threads and raises Exited synchronously before this
            // await completes, so `output` and `exitCode` are both settled by the time it returns.
            await task.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BatonException ex)
        {
            Console.Error.WriteLine($"VendorUsageCommandRun: {vendor} usage command did not run: {ex.Message}");
            return null;
        }

        if (exitCode is not 0)
        {
            Console.Error.WriteLine(
                $"VendorUsageCommandRun: {vendor} usage command exited {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "with no reported code"} -- no snapshot, last persisted one left alone.");
            return null;
        }

        var stdout = output.ToString();
        if (stdout.Trim().Length == 0)
        {
            Console.Error.WriteLine($"VendorUsageCommandRun: {vendor} usage command exited 0 but wrote no non-blank output -- no snapshot, last persisted one left alone.");
            return null;
        }

        return stdout;
    }
}
