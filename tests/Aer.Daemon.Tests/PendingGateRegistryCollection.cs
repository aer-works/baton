using Xunit;

namespace Aer.Daemon.Tests;

/// <summary>
/// Serializes every test class that touches <see cref="PendingGateRegistry"/> — a process-global
/// mutable static whose per-class ctor/Dispose <c>Clear()</c> calls wipe another class's in-flight
/// entries under xUnit's default class-level parallelism. One registry-touching class never
/// collided with itself; the second one (#1168's restart-seam tests) made the hazard real:
/// <c>Doorbell_RevokeJournalFailure_KeepsRegistryEntry_ThenRetriesViaPoll</c> failed only in the
/// parallel full-suite run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PendingGateRegistryCollection
{
    public const string Name = "PendingGateRegistry";
}
