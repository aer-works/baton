using System.Collections.Concurrent;
using Baton.Vendors.Tests.TestSupport;

namespace Baton.Vendors.Tests;

/// <summary>
/// #667: direct tests for the writer, against a throwaway directory. Every case here used to be
/// reachable only through <c>ClaudeWorkerAdapter.Resolve</c>, which meant asserting against the one
/// shared <c>claude-settings.json</c> the whole assembly resolves to.
/// </summary>
public sealed class AtomicLaunchConfigWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"launch-config-{Guid.NewGuid():N}");

    private string Path_(string name) => System.IO.Path.Combine(_directory, name);

    public AtomicLaunchConfigWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => DirectoryCleanup.DeleteRecursively(_directory);

    [Fact]
    public void A_file_that_does_not_exist_yet_is_written()
    {
        var path = Path_("settings.json");

        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        Assert.Equal("""{"hooks":"canonical"}""", File.ReadAllText(path));
    }

    /// <summary>
    /// The defect itself, with no concurrency in it. Stamping a known past mtime and requiring it to
    /// survive is exact, where a before/after comparison would depend on timestamp granularity.
    /// </summary>
    [Fact]
    public void A_write_of_the_content_already_on_disk_does_not_touch_the_file()
    {
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        AtomicLaunchConfigWriter.Write(path, content);

        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        AtomicLaunchConfigWriter.Write(path, content);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// The polarity arm of the test above, and the #543 invariant the skip must not regress: comparing
    /// content is not "write once". <c>ClaudeWorkerAdapterTests</c> asserts the same correction through
    /// <c>Resolve</c>, which is a claim about the wiring rather than the writer.
    /// </summary>
    [Fact]
    public void A_file_whose_content_has_drifted_is_rewritten()
    {
        var path = Path_("settings.json");
        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(path, stale);

        AtomicLaunchConfigWriter.Write(path, """{"hooks":"canonical"}""");

        var rewritten = File.ReadAllText(path);
        Assert.NotEqual(stale, rewritten);
        Assert.DoesNotContain("stale", rewritten);
    }

    /// <summary>
    /// A file differing only in trailing whitespace is drift, not a match. Guards the comparison
    /// against being loosened to something forgiving later: the canonical content is exact, and
    /// anything else is a file the vendor may parse differently.
    /// </summary>
    [Fact]
    public void A_file_differing_only_in_trailing_whitespace_is_rewritten()
    {
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        File.WriteAllText(path, content + "\n");

        AtomicLaunchConfigWriter.Write(path, content);

        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>
    /// A file the probe cannot read counts as differing, so the call falls through to the write
    /// instead of throwing out of the comparison. Windows arm.
    /// </summary>
    /// <remarks>
    /// The content is <b>identical</b> to what is on disk, which is what discriminates: a probe that
    /// propagated would throw before the write loop was reached. <see cref="FileShare.None"/> is
    /// enforced on Windows, so both the read and the rename fail and the assertion is on which one
    /// did. Control run, not assumed: removing the catch in <c>AlreadyHolds</c> turns this red.
    /// </remarks>
    [Fact]
    public void A_destination_the_probe_cannot_read_falls_through_to_the_write_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.None is advisory off Windows, so this cannot make a read fail here.");
        }

        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        AtomicLaunchConfigWriter.Write(path, content);

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var thrown = Record.Exception(() => AtomicLaunchConfigWriter.Write(path, content));

        Assert.NotNull(thrown);
        Assert.Contains("Move", thrown.StackTrace ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The polarity arm of the #682 fix: a rename that fails is not on its own grounds to return, or
    /// the early return would reopen the #543 invariant it sits next to -- a stale file must not stay
    /// installed with the gate silently off. <see cref="FileShare.Read"/> is what discriminates from
    /// the probe-cannot-read test above: it lets <c>AlreadyHolds</c>'s read succeed while still
    /// denying the rename, so the comparison runs against real, differing content instead of being
    /// skipped for being unreadable.
    /// </summary>
    /// <remarks>
    /// Windows arm, for the same reason as its sibling: the sharing violation this depends on is not
    /// established as portable (see this type's own remarks).
    /// </remarks>
    [Fact]
    public void A_destination_holding_different_content_still_throws_when_the_rename_cannot_land()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("FileShare.Read only blocks a rename's delete-share requirement on Windows.");
        }

        var path = Path_("settings.json");
        const string stale = """{"hooks":{"PreToolUse":[{"stale":"pre-543-content"}]}}""";
        File.WriteAllText(path, stale);

        using var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Tiny budgets force the settle-then-rethrow path immediately: this reader holds the
        // destination for the whole call, so the rename can never land and waiting out the production
        // wall-clock budget would only slow the test.
        var thrown = Record.Exception(
            () => AtomicLaunchConfigWriter.Write(
                path, """{"hooks":"canonical"}""", TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)));

        Assert.NotNull(thrown);
        Assert.Contains("Move", thrown.StackTrace ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// #682: enough concurrent cold-start writers -- no file on disk yet, so every one of them is a
    /// first writer -- exhausts <c>MaxAttempts</c> on this machine. Every writer's content is
    /// byte-identical (the real caller's is a deterministic function of
    /// <see cref="AppContext.BaseDirectory"/>; this test fixes that by construction), which is the
    /// premise the fix rests on: a writer that loses the rename does not need to win it, it needs the
    /// file to already hold what it wanted to write.
    /// </summary>
    /// <remarks>
    /// Dedicated <see cref="Thread"/>s rather than <see cref="Task.Run(Action)"/>: 40 pooled tasks
    /// parked on one <see cref="Barrier"/> would hold the thread pool hostage until injection caught
    /// up, coupling this test's timing to whatever else in the assembly wants the pool.
    /// </remarks>
    [Fact]
    public void Many_concurrent_cold_start_writers_with_identical_content_do_not_throw()
    {
        // Deliberately NOT skipped off Windows, unlike the siblings above: "no writer throws" is a
        // claim worth holding on every platform. But the CONTENTION only reproduces where renames
        // take the sharing violation (#682 was measured on Windows), so off Windows this asserts
        // the happy path, not the retry path -- the reproduction is Windows-only.
        var path = Path_("settings.json");
        const string content = """{"hooks":"canonical"}""";
        const int writerCount = 40;

        using var barrier = new Barrier(writerCount);
        var exceptions = new ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, writerCount).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                AtomicLaunchConfigWriter.Write(path, content);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        foreach (var writer in writers)
        {
            writer.Start();
        }

        foreach (var writer in writers)
        {
            writer.Join();
        }

        Assert.Empty(exceptions);
        Assert.Equal(content, File.ReadAllText(path));
    }

    /// <summary>
    /// The same claim on Unix, where the observable differs: a mode-000 file fails the probe's read
    /// but not the rename, so the call has to <i>succeed</i> rather than throw from somewhere else.
    /// </summary>
    /// <remarks>
    /// Skips when the read succeeds anyway — running as root defeats the permission bits, and a pass
    /// under those conditions would prove nothing.
    /// </remarks>
    [Fact]
    public void A_destination_the_probe_cannot_read_falls_through_to_the_write_on_unix()
    {
        // Guarded with else rather than an early return so CA1416 can see that SetUnixFileMode is
        // unreachable on Windows -- Assert.Skip throws, but nothing tells the analyzer that.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix mode bits do not apply; the Windows arm covers this platform.");
        }
        else
        {
            var path = Path_("settings.json");
            const string content = """{"hooks":"canonical"}""";
            AtomicLaunchConfigWriter.Write(path, content);
            File.SetUnixFileMode(path, UnixFileMode.None);

            if (Record.Exception(() => File.ReadAllText(path)) is null)
            {
                Assert.Skip("The mode-000 file is still readable (running as root?), so the probe cannot fail.");
            }

            AtomicLaunchConfigWriter.Write(path, content);

            Assert.Equal(content, File.ReadAllText(path));
        }
    }
}
