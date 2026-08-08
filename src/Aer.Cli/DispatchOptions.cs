namespace Aer.Cli;

/// <summary>
/// Parsed arguments for the <c>aer dispatch</c> command — just the inputs. <see cref="DispatchCommand"/>
/// resolves <paramref name="Name"/> against the catalogs (one namespace) and drives the result through
/// the same pump <c>aer run</c> uses; what a role vs a template means, and why, lives there.
/// </summary>
/// <param name="Name">The catalog role or workflow template to dispatch (e.g. <c>review</c>), resolved by <see cref="DispatchCommand"/>.</param>
/// <param name="SpecFilePath">
/// The file whose contents are the task prompt — what a <em>role</em> is asked to do. Required for a
/// role, and rejected for a template (a template's phases already carry their instructions). Null when
/// not supplied.
/// </param>
/// <param name="RoomDirectoryPath">
/// Where this dispatch's durable state lives. Defaults to a fresh, uniquely-named directory per
/// invocation (see <see cref="DispatchOptionsParser"/>) so a repeated self-dispatch runs anew rather
/// than resuming — and so replaying a prior terminal snapshot — the way an orchestrator (#778) issues
/// the same name many times. Pass an explicit value to resume a specific interrupted dispatch.
/// </param>
/// <param name="Adapter">
/// A vendor adapter to run every role/phase on instead of its tier default — the escape hatch. Null
/// keeps each role's own tier-resolved adapter.
/// </param>
/// <param name="WorkflowId">A label forwarded to the run; defaults to the materialized template id.</param>
/// <param name="WorkspaceDirectory">
/// The directory a dispatched role runs in and may read — pinned onto its binding so a vendor that
/// ignores the process cwd (agy <c>-p</c>, #491) is still handed the project via <c>--add-dir</c>. Null
/// resolves to the process cwd in <see cref="DispatchCommand"/>, which is the common case (dispatching
/// from the repo). Without this a role dispatched to read the repo was given no path to it and every
/// repo read was auto-denied (#1083).
/// </param>
/// <param name="Model">
/// The model axis, independent of the role's tier ([0017]/[0033]: vendor/model/effort are three
/// separate axes). Null keeps the tier's model — except that an <paramref name="Adapter"/> override to
/// a different vendor drops the tier's vendor-specific model for that vendor's default (#1082).
/// </param>
/// <param name="Effort">The effort axis, independent of the role's tier ([0017]/[0023]); null keeps the tier's effort.</param>
public sealed record DispatchOptions(
    string Name,
    string? SpecFilePath,
    string RoomDirectoryPath,
    string? Adapter = null,
    string? WorkflowId = null,
    string? WorkspaceDirectory = null,
    string? Model = null,
    string? Effort = null);
