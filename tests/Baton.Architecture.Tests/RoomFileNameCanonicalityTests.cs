using System.Text.RegularExpressions;

namespace Baton.Architecture.Tests;

/// <summary>
/// #1271: the four filenames that identify a room directory (<c>room.jsonl</c>, <c>flow.jsonl</c>,
/// <c>snapshot.json</c>, <c>flow.lock</c>) have exactly one canonical home —
/// <see cref="Baton.Status.BatonPaths"/> — after having been re-declared as private consts across six
/// CLI files. Decision 0057 rule 4 names these four together as its live-room evidence test
/// (<see cref="Baton.Status.BatonPaths.RoomEvidenceFileNames"/> is that set, spelled once); the
/// component that evaluated it, the desktop bindings editor, was deleted in the spec v2.0 reset
/// (#1397) and no current caller re-implements the check. The risk the issue named — a rename
/// silently desyncing the evidence test from the writers that produce it — is dormant rather than
/// eliminated, since a future rule-4 consumer would read the same drifted literal a writer just
/// renamed. This test is the tripwire regardless: a stray literal reintroduced anywhere outside the
/// canonical home fails the build the moment it lands. Mirrors <see cref="FileDeleteHygieneTests"/>'s
/// literal-scan-over-<c>src</c> shape.
/// </summary>
public class RoomFileNameCanonicalityTests
{
    [Fact]
    public void No_source_file_outside_BatonPaths_declares_a_room_evidence_filename_literal()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var regex = new Regex(@"""(room\.jsonl|flow\.jsonl|snapshot\.json|flow\.lock)""");

        // The one file allowed to declare these literals — everything else must reference
        // Baton.Status.BatonPaths's consts instead.
        const string allowed = "Baton/Status/BatonPaths.cs";

        var offenders = new List<string>();

        foreach (var filePath in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(srcDir, filePath).Replace('\\', '/');
            if (relativePath.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = File.ReadAllText(filePath);
            if (regex.IsMatch(content))
            {
                offenders.Add(relativePath);
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Found room-evidence filename literal(s) declared outside Baton.Status.BatonPaths in: " +
            $"{string.Join(", ", offenders)}. Reference BatonPaths.RoomLogFileName / FlowLogFileName / " +
            "SnapshotFileName / FlowLockFileName instead of restating the literal (#1271) — a drifted " +
            "restatement is how decision 0057 rule 4's live-room detection fails silently.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Baton.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate the repo root (Baton.slnx) by walking up from " + AppContext.BaseDirectory);
    }
}
