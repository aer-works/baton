using Baton.Vendors.Tests.TestSupport;
namespace Baton.Vendors.Tests;

/// <summary>
/// M23 Phase 3's per-machine profile mapping (#272): <see cref="BatonProfileStore"/>'s load/save round
/// trip and its "missing file is empty, malformed file throws" distinction — see the type's own
/// remarks for why those two failure shapes are treated differently.
/// </summary>
public class BatonProfileStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"baton-profiles-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Loading_a_missing_file_resolves_to_an_empty_map()
    {
        var path = TempPath();

        var profiles = await BatonProfileStore.LoadAsync(path, TestContext.Current.CancellationToken);

        Assert.Empty(profiles);
    }

    [Fact]
    public async Task Saving_then_loading_round_trips_the_map()
    {
        var path = TempPath();
        try
        {
            var original = new Dictionary<string, string>
            {
                ["myproject"] = "/home/user/dev/myproject",
                ["other"] = "/home/user/dev/other",
            };

            await BatonProfileStore.SaveAsync(original, path, TestContext.Current.CancellationToken);
            var loaded = await BatonProfileStore.LoadAsync(path, TestContext.Current.CancellationToken);

            Assert.Equal(original, loaded);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public async Task Saving_creates_the_parent_directory_if_it_does_not_exist_yet()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"baton-profiles-dir-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "profiles.json");
        try
        {
            await BatonProfileStore.SaveAsync(
                new Dictionary<string, string> { ["p"] = "/x" }, path, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            DirectoryCleanup.DeleteRecursively(directory);
        }
    }

    [Fact]
    public async Task Loading_a_malformed_file_throws_rather_than_silently_resolving_to_empty()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json", TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<ProfileStoreException>(
                () => BatonProfileStore.LoadAsync(path, TestContext.Current.CancellationToken));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            FileCleanup.Delete(path);
        }
    }

    [Fact]
    public void DefaultPath_lives_under_a_dot_baton_directory_in_the_user_profile()
    {
        Assert.EndsWith(Path.Combine(".baton", "profiles.json"), BatonProfileStore.DefaultPath);
    }
}
