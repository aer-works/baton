using Baton.Status;

namespace Baton.Vendors.Tests.TestSupport;

/// <summary>
/// A throwaway <see cref="BatonPaths.Root"/> for a test class, scoped through
/// <see cref="BatonEnvironmentSnapshot.BeginScope"/> — never a process environment variable, so it is
/// parallel-safe and needs no assembly-wide collection enrollment.
/// </summary>
/// <remarks>
/// #1166 fix round (H1): this assembly's mirror of
/// <c>Baton.Cli.Tests.TestSupport.IsolatedBatonHome</c> — not shared cross-project because
/// <c>BatonEnvironmentSnapshot.BeginScope</c> is internal (<c>InternalsVisibleTo</c>-only) and
/// <c>tests/Shared</c> is linked, dependency-free source compiled into every test project including
/// ones (<c>Baton.Architecture.Tests</c>, <c>Baton.VendorProbe.Tests</c>, <c>Baton.CrashTestHost</c>)
/// that reference neither <c>Baton</c> nor carry that grant. Added so
/// <c>TemplateDispatchabilityTests</c> can give itself its own <c>project-ceilings.json</c> instead of
/// racing every other class writing through <c>AtomicLaunchConfigWriter</c> to the assembly's one
/// shared <c>BATON_HOME</c> (<c>tests/Shared/BatonHomeRedirect.cs</c>).
/// </remarks>
public sealed class IsolatedBatonHome : IDisposable
{
    private readonly IDisposable _scope;

    public IsolatedBatonHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"baton-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        _scope = BatonEnvironmentSnapshot.BeginScope(BatonEnvironmentSnapshot.Blank with { HomeOverride = Path });
    }

    /// <summary>The temp directory standing in for <c>~/.baton</c>.</summary>
    public string Path { get; }

    public void Dispose()
    {
        _scope.Dispose();
        DirectoryCleanup.DeleteRecursively(Path);
    }
}
