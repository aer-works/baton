namespace Baton.Tests.Projection;

/// <summary>
/// Serializes every test class that touches the process-global <see cref="Console.Error"/> stream —
/// either by swapping it via <see cref="Console.SetError(System.IO.TextWriter)"/> to capture
/// loud-fallback output, or by driving production code that writes to it directly without swapping
/// it. Two SetError-swapping classes running in parallel interleave — one test's SetError lands
/// between another's capture and restore, and each reads the other's output (#967). An unswapped
/// direct writer racing a swap is the same defect from the other side (#1607): its write can land in
/// whichever `TextWriter` happens to be installed at that instant, including another test's capture
/// buffer. Classes in this collection stay sequential relative to each other; the rest of the
/// assembly keeps xUnit's normal parallelism.
/// </summary>
[CollectionDefinition(Name)]
public class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
