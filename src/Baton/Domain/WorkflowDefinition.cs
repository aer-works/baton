using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// Declarative structure only — no loops, no conditionals, no runtime logic. Editable
/// and versionable; not itself bound to any running task. <see cref="WorkflowTemplateVersion"/>
/// increments on every edit that is instantiated from.
/// </summary>
public sealed record WorkflowDefinition(
    WorkflowTemplateId WorkflowTemplateId,
    int WorkflowTemplateVersion,
    IReadOnlyList<WorkflowStepDefinition> Steps);

/// <summary>A single step in a <see cref="WorkflowDefinition"/> template.</summary>
public sealed record WorkflowStepDefinition(
    StepId StepId,
    string Worker,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<StepId> DependsOn,
    RetryPolicy RetryPolicy,
    PausePoint? PausePoint = null);

/// <summary>Governs whether a failure triggers a new <see cref="ExecutionRequest"/>.</summary>
[method: JsonConstructor]
public sealed record RetryPolicy(int MaxAttempts, BackoffPolicy Backoff)
{
    /// <summary>Backoff policy applied between retry attempts. Defaults to <see cref="BackoffPolicy.Default"/>.</summary>
    public BackoffPolicy Backoff { get; init; } = Backoff ?? BackoffPolicy.Default;

    /// <summary>Constructs a <see cref="RetryPolicy"/> with default backoff strategy (<see cref="BackoffPolicy.Default"/>).</summary>
    public RetryPolicy(int MaxAttempts) : this(MaxAttempts, BackoffPolicy.Default) { }
}

/// <summary>Controls the random jitter applied to retry backoff delays (#712).</summary>
public enum JitterMode
{
    /// <summary>No jitter is applied; delays are deterministic exponential intervals.</summary>
    None,

    /// <summary>Delay is uniformly randomized in [delay / 2, delay].</summary>
    Half
}

/// <summary>Configures backoff growth, cap, and jitter strategy for retry delays (#712).</summary>
[JsonConverter(typeof(BackoffPolicyJsonConverter))]
public sealed record BackoffPolicy(TimeSpan Initial, double Multiplier, TimeSpan Cap, JitterMode Jitter)
{
    /// <summary>Preset zero-delay policy (Initial = 0, Multiplier = 1, Cap = 0, Jitter = None).</summary>
    public static readonly BackoffPolicy None = new(TimeSpan.Zero, 1, TimeSpan.Zero, JitterMode.None);

    /// <summary>Preset brisk policy (Initial = 200 ms, Multiplier = 2, Cap = 5 s, Jitter = Half).</summary>
    public static readonly BackoffPolicy Brisk = new(TimeSpan.FromMilliseconds(200), 2, TimeSpan.FromSeconds(5), JitterMode.Half);

    /// <summary>Preset steady policy (Initial = 1 s, Multiplier = 3, Cap = 60 s, Jitter = Half).</summary>
    public static readonly BackoffPolicy Steady = new(TimeSpan.FromSeconds(1), 3, TimeSpan.FromSeconds(60), JitterMode.Half);

    /// <summary>Preset patient policy (Initial = 5 s, Multiplier = 3, Cap = 15 min, Jitter = Half).</summary>
    public static readonly BackoffPolicy Patient = new(TimeSpan.FromSeconds(5), 3, TimeSpan.FromMinutes(15), JitterMode.Half);

    /// <summary>Default backoff policy applied when backoff is omitted in workflow templates.</summary>
    public static BackoffPolicy Default => Steady;

    /// <summary>
    /// Computes the pure delay for attempt index <paramref name="attempt"/> (1-based: delay before attempt n+1),
    /// using exponential growth, cap clamp, and jitter from caller-supplied <paramref name="sample"/> in [0, 1).
    /// </summary>
    public TimeSpan DelayFor(int attempt, double sample)
    {
        if (Cap == TimeSpan.Zero || Initial == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        int exponent = Math.Max(0, attempt - 1);
        double rawMs = Initial.TotalMilliseconds * Math.Pow(Multiplier, exponent);
        double cappedMs = Math.Min(rawMs, Cap.TotalMilliseconds);

        double finalMs = Jitter switch
        {
            JitterMode.Half => cappedMs * (0.5 + 0.5 * Math.Clamp(sample, 0.0, 1.0)),
            _ => cappedMs
        };

        return TimeSpan.FromMilliseconds(finalMs);
    }
}

/// <summary>
/// Distinguishes <em>why</em> a <see cref="PausePoint"/> stopped the DAG, so the two human acts a
/// pause can demand — answering a question versus approving finished work — render and filter as the
/// separate states they are (issue #334). A pause's kind is a static property of the step
/// that declares the pause point: it is invariant per declaration, never a per-execution worker
/// signal (execution outcomes carry no done/needs-input flag — see <see cref="FlowEvent.ExecutionSucceeded"/>).
/// It is therefore derived from the bound <see cref="WorkflowDefinitionSnapshot"/> at projection time
/// and carried by no <see cref="FlowEvent"/>; the snapshot is itself part of the durable, write-once
/// record, so no event-format change or replay migration is required.
/// </summary>
public enum PausePointKind
{
    /// <summary>
    /// The step ran to a terminal outcome and its result awaits human review/approval before the DAG
    /// proceeds — the approval gate. The historical meaning of every pause, and deliberately the
    /// zero value: a snapshot serialized before this field existed omits it, and STJ materializes the
    /// missing value as <c>default(PausePointKind)</c>, which must land here for replay to stay correct.
    /// </summary>
    ReadyForReview = 0,

    /// <summary>
    /// The step is an interactive turn paused ready for the operator's next message. It is not
    /// "awaiting approval," it is "awaiting input" — an ordinary chat turn, which must not demand a
    /// review decision. Declared only by interactive-session steps (see
    /// <c>Baton.Vendors.InteractiveSessionMaterializer</c>), never inferred from conversation content
    /// (Architecture Rule 1).
    /// </summary>
    NeedsInput = 1,
}

/// <summary>
/// Declared on a step to have Flow append <see cref="FlowEvent.WorkflowPaused"/> instead of immediately
/// evaluating downstream readiness when the step reaches a terminal outcome.
/// </summary>
/// <param name="SupersedeTargets">
/// The set of earlier <see cref="StepId"/>s a <see cref="DecisionType.Supersede"/> decision made at
/// this pause point may target. Every entry shall be a unique, transitive ancestor of the step
/// declaring this pause point. Empty means this pause point supports
/// <see cref="DecisionType.Resume"/>/<see cref="DecisionType.Reject"/>/<see cref="DecisionType.RetryWithRevision"/>
/// only.
/// </param>
/// <param name="Kind">
/// Which human act this pause demands (issue #334). Defaults to <see cref="PausePointKind.ReadyForReview"/>
/// so every authored review gate and every pause persisted before this field existed keeps its
/// original approval-gate meaning; only interactive-session steps opt into
/// <see cref="PausePointKind.NeedsInput"/>.
/// </param>
public sealed record PausePoint(
    IReadOnlyList<StepId> SupersedeTargets,
    PausePointKind Kind = PausePointKind.ReadyForReview);
