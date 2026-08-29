namespace Aer.Flow.Domain;

/// <summary>
/// The originate scope a grant carries: which templates the holder may originate runs
/// from — an explicit list or any — and the per-origination budget. Exactly the fields the
/// recorded design names; a grant is otherwise scoped by the room whose journal records it.
/// </summary>
public sealed record GrantScope(
    IReadOnlyList<WorkflowTemplateId>? TemplateIds = null,
    bool AnyTemplates = false,
    TimeSpan? Budget = null);
