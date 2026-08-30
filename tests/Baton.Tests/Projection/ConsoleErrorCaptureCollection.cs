namespace Baton.Tests.Projection;

/// <summary>
/// Serializes every test class that captures loud-fallback stderr by swapping the process-global
/// writer via <see cref="Console.SetError(System.IO.TextWriter)"/>. Two such classes running in
/// parallel interleave — one test's SetError lands between another's capture and restore, and each
/// reads the other's output (#967). Classes in this collection stay sequential relative to each
/// other; the rest of the assembly keeps xUnit's normal parallelism.
/// </summary>
[CollectionDefinition(Name)]
public class ConsoleErrorCaptureCollection
{
    public const string Name = "console-error-capture";
}
