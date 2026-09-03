using System.Security.Cryptography;
using System.Text;
using Baton.Status;

namespace Baton.Tests.Status;

/// <summary>
/// <see cref="MutexGuardedFileLock"/> is <c>RoomRegistryStore</c>'s own mutex primitive, extracted so
/// <see cref="QuotaLedgerStore"/> (#1570) can share it. The name-format pin below is the regression
/// guard for that extraction — see the type's own remarks for why a renamed lock is a silent hazard,
/// not just a cosmetic diff: every in-process test would still pass against it.
/// </summary>
public sealed class MutexGuardedFileLockTests
{
    [Fact]
    public void BuildMutexName_matches_the_literal_format_RoomRegistryStore_shipped_before_the_extraction()
    {
        var path = @"C:\Users\test\.baton\room-registry.jsonl";
        var expectedDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(BatonPaths.RecordKey(path).ToUpperInvariant())));
        var expected = $"Global\\baton-room-registry-{expectedDigest}";

        var actual = MutexGuardedFileLock.BuildMutexName(path, "baton-room-registry");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildMutexName_is_insensitive_to_path_spelling_the_same_way_RecordKey_is()
    {
        var forward = MutexGuardedFileLock.BuildMutexName(@"C:\home\.baton\room-registry.jsonl", "baton-room-registry");
        var trailingSeparator = MutexGuardedFileLock.BuildMutexName(@"C:\home\.baton\room-registry.jsonl\", "baton-room-registry");
        var differentCasing = MutexGuardedFileLock.BuildMutexName(@"c:\HOME\.baton\ROOM-REGISTRY.jsonl", "baton-room-registry");

        Assert.Equal(forward, trailingSeparator);
        Assert.Equal(forward, differentCasing);
    }

    [Fact]
    public void Distinct_lock_name_prefixes_against_the_same_path_never_collide()
    {
        var path = @"C:\home\.baton\shared-name.jsonl";

        var registryName = MutexGuardedFileLock.BuildMutexName(path, "baton-room-registry");
        var ledgerName = MutexGuardedFileLock.BuildMutexName(path, "baton-quota-ledger");

        Assert.NotEqual(registryName, ledgerName);
    }

    [Fact]
    public void RunUnderLock_returns_the_action_result_and_releases_for_the_next_caller()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");

        var first = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => 41);
        var second = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => first + 1);

        Assert.Equal(42, second);
    }

    [Fact]
    public void RunUnderLock_releases_even_when_the_action_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mutex-guarded-{Guid.NewGuid():N}.jsonl");

        Assert.Throws<InvalidOperationException>(() =>
            MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () =>
            {
                throw new InvalidOperationException("boom");
            }));

        // If the throwing call above leaked the mutex, this second acquisition on the same
        // (path, prefix) would hang until the test's own timeout rather than return promptly.
        var result = MutexGuardedFileLock.RunUnderLock(path, "test-prefix", TimeSpan.FromSeconds(5), () => "released");
        Assert.Equal("released", result);
    }
}
