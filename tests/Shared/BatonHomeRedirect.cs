using System.Runtime.CompilerServices;

namespace Baton.Tests.Shared;

/// <summary>
/// Redirects AER's storage root (the <c>BATON_HOME</c> environment variable that
/// <c>Baton.Flow.Status.BatonPaths.Root</c> reads) to a throwaway per-process temp directory <b>before any
/// test runs</b>, so no test in this assembly reads or writes the developer's — or the CI runner's —
/// real <c>~/.baton</c>.
/// </summary>
/// <remarks>
/// <para>
/// Linked into every test project through <c>tests/Directory.Build.props</c>; each test assembly is
/// its own process (a separate test host per project), so each gets its own initializer, its own
/// temp root, and its own cleanup — they never collide.
/// </para>
/// <para>
/// <b>Why a <see cref="ModuleInitializerAttribute"/> and not a fixture:</b> the redirect must happen
/// before the first read of <c>BatonPaths.Root</c>, and a module initializer runs at assembly load,
/// ahead of any test or fixture. Because <c>BatonPaths.Root</c> re-resolves the environment on every
/// access (it deliberately never caches), setting <c>BATON_HOME</c> here is enough — nothing captured
/// the root at type-load.
/// </para>
/// <para>
/// This is the mechanism, not the guarantee. Cleanup runs only on a graceful process exit, and a
/// leak (a store added later that bypasses <c>BatonPaths</c>) would escape it. The guarantee is the CI
/// job's before/after snapshot of the real <c>~/.baton</c> — see <c>.github/workflows/ci.yml</c>.
/// </para>
/// </remarks>
internal static class BatonHomeRedirect
{
    private const string HomeEnvironmentVariable = "BATON_HOME";

    [ModuleInitializer]
    internal static void Redirect()
    {
        // Respect an BATON_HOME already set by an outer process: a parent test may spawn this one as a
        // child (e.g. the crash-test host), and it expects the child to write into the *parent's*
        // root. Overriding it here would split their storage in two. Only redirect when unset.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HomeEnvironmentVariable)))
        {
            return;
        }

        // The root ends in ".baton" so it mirrors the production {UserProfile}/.baton exactly: tests that
        // assert storage lives under a directory literally named ".baton" (a real, user-facing contract
        // — people look for ~/.baton) keep passing without being rewritten into tautologies.
        var assemblyName = typeof(BatonHomeRedirect).Assembly.GetName().Name ?? "baton-tests";
        var runRoot = Path.Combine(
            Path.GetTempPath(), "baton-test-home", $"{assemblyName}-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var storageRoot = Path.Combine(runRoot, ".baton");
        Directory.CreateDirectory(storageRoot);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, storageRoot);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                DirectoryCleanup.DeleteRecursively(runRoot);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup at shutdown. The CI snapshot guard is the real guarantee that
                // nothing escaped to the real ~/.baton, so a failure to delete the temp copy is logged,
                // not fatal — swallowing it silently would violate the repo's error-handling rule.
                Console.Error.WriteLine($"[BatonHomeRedirect] temp-root cleanup failed for '{runRoot}': {ex.Message}");
            }
        };
    }
}
