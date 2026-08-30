namespace Baton.Tests.Shared;

/// <summary>
/// The single-file counterpart to <see cref="DirectoryCleanup"/>; both route a test's delete through
/// <see cref="CleanupRetry"/>, which documents the #295 flake they guard and the teardown-vs-setup
/// contract the two methods pick between. <c>File.Delete</c> already no-ops on a missing file, so only a
/// missing parent directory needs the "already gone" handling <see cref="CleanupRetry"/> provides.
/// <see cref="Delete"/> is teardown (swallowing); <see cref="EnsureDeleted"/> is setup (rethrowing).
/// </summary>
internal static class FileCleanup
{
    public static void Delete(string path) =>
        CleanupRetry.Run(() => File.Delete(path), swallowOnFinal: true);

    public static void EnsureDeleted(string path) =>
        CleanupRetry.Run(() => File.Delete(path), swallowOnFinal: false);
}
