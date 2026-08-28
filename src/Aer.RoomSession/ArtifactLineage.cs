using Aer.Flow.Domain;

namespace Aer.RoomSession;

/// <summary>
/// Every execution's artifact output directory, plus — for each of its declared inputs — which
/// upstream execution produced it (UI spec §10; M14 Phase 4, issue #121). Walked directly from the
/// same Flow Event Store <see cref="RoomProjectionLoader"/> already reads, the same
/// presentation-layer-derived-from-durable-facts shape <see cref="ExecutionHistory"/> established
/// (M14 Phase 2): never a new stored fact, never a re-derivation of which execution is a step's
/// *current* one — <see cref="ArtifactInputLink"/> instead comes straight from each recorded
/// <see cref="ExecutionRequest.UpstreamExecutionIds"/>, so a retried or superseded step's earlier
/// executions still show exactly which upstream attempt fed them, not today's latest.
/// </summary>
public sealed record ArtifactLineage(IReadOnlyList<ExecutionArtifacts> Executions);

/// <summary>
/// One execution's artifact-directory contents (spec §16's <c>artifacts/execution_{N}/</c>) as they
/// actually exist on disk — never the step's declared <c>Outputs</c> list, which only says what a
/// worker was contracted to produce, not what is really there. <see cref="OutputFiles"/> is sorted
/// ordinally rather than left in <see cref="Directory.GetFiles(string)"/>'s platform-dependent
/// order, since UI spec §11 demands identical projected state across all three CI OSes.
/// </summary>
public sealed record ExecutionArtifacts(
    ExecutionId ExecutionId,
    StepId? StepId,
    string Worker,
    IReadOnlyList<string> OutputFiles,
    IReadOnlyList<ArtifactInputLink> Inputs,
    // #1191: what this execution was CONTRACTED to produce, read off its own ExecutionRequest —
    // the same list MutationInterface turns into the ProducedOutputs that ContractValidator
    // satisfies with File.Exists, so it is the per-execution truth rather than a nearby
    // approximation of it. OutputFiles above is every file that ended up in the directory, the
    // worker's prompt included; the two are not the same question and a surface that wants "the
    // thing this step produced" has to ask this one.
    IReadOnlyList<string> DeclaredOutputs);

/// <summary>
/// One resolved input of an execution: the declared input name, which step's declared
/// <c>Outputs</c> produces it (found by walking the snapshot's static <c>DependsOn</c>/<c>Outputs</c>
/// shape — the same structural lookup <see cref="Aer.Flow.Artifacts.ArtifactManager"/> does
/// internally, but here read directly rather than recomputed against current state), and exactly
/// which of that producer's executions this request's <see cref="ExecutionRequest.UpstreamExecutionIds"/>
/// named — the durable, immutable fact that makes lineage navigable without staleness ever entering
/// the picture.
/// </summary>
public sealed record ArtifactInputLink(string InputName, StepId ProducerStepId, ExecutionId ProducerExecutionId);
