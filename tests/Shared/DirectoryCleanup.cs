namespace Baton.Tests.Shared;

/// <summary>
/// Recursive test-directory deletes routed through <see cref="CleanupRetry"/>, split into teardown and
/// setup forms like <see cref="FileCleanup"/>. Before 2026-08-02 <see cref="DeleteRecursively"/> rethrew
/// on the final attempt — a latent finally-masking hazard fixed alongside adding
/// <see cref="FileCleanup"/> (#918).
/// </summary>
internal static class DirectoryCleanup
{
    public static void DeleteRecursively(string path) =>
        CleanupRetry.Run(() => Directory.Delete(path, recursive: true), swallowOnFinal: true);

    public static void EnsureDeletedRecursively(string path) =>
        CleanupRetry.Run(() => Directory.Delete(path, recursive: true), swallowOnFinal: false);
}
