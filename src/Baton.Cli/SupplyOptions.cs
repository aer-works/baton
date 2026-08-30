namespace Baton.Cli;

/// <summary>
/// Parsed arguments for <c>baton supply</c> (M12 Phase 3), the supplementary-artifact surface
/// exposed on the CLI. Mints a step-less <see cref="Baton.Mutation.WorkerBinding.NonProcess"/>
/// execution and populates it from <paramref name="SourceFilePath"/> in the same call, so the
/// printed <see cref="Baton.Domain.ExecutionId"/> is immediately usable as a
/// <c>--supplementary</c> argument to <c>baton decide</c> — no separate settling call is needed
/// (unlike a mid-DAG human step, whose completion only a later <c>baton run</c> pump can detect).
/// </summary>
/// <param name="RoomDirectoryPath">An already-started room's durable state directory.</param>
/// <param name="Worker">
/// The worker role to mint under (e.g. <c>"human"</c>). Worker-binding config files only ever
/// resolve to <see cref="Baton.Mutation.WorkerBinding.Process"/> (M11's decision of record), so
/// this command constructs the <see cref="Baton.Mutation.WorkerBinding.NonProcess"/> binding
/// directly from <paramref name="OutputName"/> rather than looking one up in the bindings file.
/// </param>
/// <param name="OutputName">The single declared output name this supplementary execution produces.</param>
/// <param name="SourceFilePath">An existing file copied into the assigned output directory under <paramref name="OutputName"/>.</param>
/// <param name="BindingsFilePath">The worker-binding config file — resolved for its Process entries only.</param>
/// <param name="WorkflowId">See <see cref="CancelOptions.WorkflowId"/> — every mutation command shares this fallback.</param>
public sealed record SupplyOptions(
    string RoomDirectoryPath,
    string Worker,
    string OutputName,
    string SourceFilePath,
    string BindingsFilePath,
    string? WorkflowId = null);
