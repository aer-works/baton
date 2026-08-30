namespace Baton.Flow.Domain;

/// <summary>
/// A worker's optional self-reported judgment of whether retrying an <see cref="FlowEvent.ExecutionFailed"/>
/// would help (spec/baton.md §7). The only vocabulary Flow itself understands for this purpose — a
/// freeform string would become a de facto API the moment scheduling depended on its exact
/// spelling. Absent or unrecognized is treated as <see cref="Retryable"/>.
/// </summary>
public enum FailureClassification
{
    Retryable,
    Permanent,
    ExhaustedUntil,
    ToolDenied,
}
