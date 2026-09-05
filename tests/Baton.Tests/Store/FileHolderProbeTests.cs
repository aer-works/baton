using Baton.Store;
using Baton.Tests.Shared;

namespace Baton.Tests.Store;

/// <summary>
/// <see cref="FileHolderProbe"/> is the diagnostic that enriches a sharing-violation
/// (<see cref="FlowJournalHeldException"/>, #398 class) with the name of the process actually holding
/// the file. These prove it reads a real, live handle rather than returning a canned string: probing a
/// file this test process holds exclusively must name this process's own pid, and probing a file nobody
/// holds must not.
/// </summary>
public class FileHolderProbeTests
{
    [Fact]
    public void Names_the_process_that_holds_a_file_open_exclusively()
    {
        var path = Path.Combine(Path.GetTempPath(), $"holder-probe-{Guid.NewGuid():N}.tmp");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            // The only holder is this test process. Naming our own pid proves the probe read the live
            // handle table, not a placeholder — the discriminating check. The closing paren is part of
            // the match so a pid that is a numeric prefix of another's (123 vs 1234) can't false-match.
            Assert.Contains($"(pid {Environment.ProcessId})", FileHolderProbe.DescribeHolders(path));
        }

        FileCleanup.Delete(path);
    }

    [Fact]
    public void Does_not_name_this_process_for_a_file_it_does_not_hold()
    {
        var path = Path.Combine(Path.GetTempPath(), $"holder-probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "x"); // written and closed — this process holds no handle to it now
        try
        {
            // Negative control: robust even if an external scanner transiently grabs the file (it would
            // name the scanner, never us). A blind probe returning a canned "held by pid <self>" fails
            // here. The closing paren guards against a scanner whose pid our pid is a numeric prefix of.
            Assert.DoesNotContain($"(pid {Environment.ProcessId})", FileHolderProbe.DescribeHolders(path));
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }
}
