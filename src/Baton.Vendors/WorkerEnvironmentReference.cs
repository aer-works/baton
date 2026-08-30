namespace Baton.Vendors;

/// <summary>
/// How an AER-computed environment variable reference is written into prompt text
/// (<c>%BATON_OUTPUT_DIR%</c> on Windows, <c>$BATON_OUTPUT_DIR</c> elsewhere). Writing only — the
/// expansion, and the full token grammar including the <c>${NAME}</c> form this helper never emits,
/// live in one place: <c>CoreDispatcher.VariableToken</c>, which expands args and prompt text at
/// dispatch. The worker's shell is not the expander, though the same names are also set in the
/// child environment.
/// </summary>
/// <remarks>
/// One copy, because there were three: both vendor adapters carried an identical private helper, and
/// #650 needed a third when the interactive session's write instruction moved out of its contract and
/// into its prompt template. The syntax is a property of the shell the worker runs in rather than of
/// any one vendor, so sharing it does not cross Adapter Isolation.
/// </remarks>
internal static class WorkerEnvironmentReference
{
    internal static string For(string name, bool isWindows) => isWindows ? $"%{name}%" : $"${name}";

    internal static string For(string name) => For(name, OperatingSystem.IsWindows());
}
