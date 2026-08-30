namespace Baton.Cli.Tests;

/// <summary>
/// Serialises the classes that depend on the process working directory. <see cref="OutboxPathTests"/>
/// sets it to prove a relative candidate resolves <i>inside</i> the outbox; every parser class here
/// resolves its own relative paths against it. It is process-global, and xUnit runs classes in
/// parallel, so without this the parsers can read a cwd the outbox test is borrowing.
/// </summary>
/// <remarks>
/// Latent since #681 gave the parsers a resolved room directory; observed once as
/// <c>SupplyOptionsParserTests.Options_may_precede_the_positional_room_directory</c> expecting a path
/// under the outbox's temp directory.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "working-directory";
}
