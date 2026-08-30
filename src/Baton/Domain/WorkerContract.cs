using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// A logical execution target (e.g. <c>claude</c>, <c>agy</c>, <c>git</c>) bound to a typed
/// contract, not a vendor name. A <see cref="WorkflowStepDefinition"/> declares which
/// contract it requires; the concrete binary is resolved via configuration external to the
/// workflow.
/// </summary>
public sealed record WorkerContract(
    string WorkerName,
    IReadOnlyList<string> RequiredInputs,
    IReadOnlyList<ProducedOutput> ProducedOutputs,
    IReadOnlyList<string> OptionalMetadata);

/// <summary>
/// The one statement of why a declared output name may not begin with a dot, and the one wording
/// every rejection uses (#1345).
/// <para>
/// Four production sites reject the same thing — <see cref="ProducedOutput"/>'s constructor,
/// <c>WorkflowDefinitionValidator</c>, <c>WorkerBindingConfigParser</c> and
/// <c>WorkerRoleCatalog</c> — and each had written the sentence out again, in three different
/// phrasings, with three test files asserting a substring of it. All four also said the namespace
/// was "reserved for engine stream logs", which undersells the rule: the whole leading-dot namespace
/// is reserved, not just the names
/// <see cref="Baton.Dispatch.ExecutionStreamLogger.IsStreamLogFileName"/> happens to use today.
/// </para>
/// <para>
/// The reservation covers <em>declaring</em> an output. A worker may still write an undeclared
/// dot-named file into its output directory, and that file is a document like any other — which is
/// why the stream-log filter is four exact names rather than a prefix test.
/// </para>
/// </summary>
public static class ReservedOutputNames
{
    public const string LeadingDot = ".";

    public static bool IsReserved(string? name) => name is not null && name.StartsWith(LeadingDot, StringComparison.Ordinal);

    /// <summary>The shared rejection clause. Callers prefix it with their own context.</summary>
    public const string RejectionClause =
        "a declared output cannot start with '.' — that namespace is reserved for engine-written files, such as ExecutionStreamLogger's stream logs";
}

/// <summary>A named output file role a <see cref="WorkerContract"/> requires.</summary>
/// <param name="Schema">
/// A declared document shape the file must parse as (decision 0043) — the structural
/// sibling of <paramref name="Condition"/>. Serialized only when set, so contracts that predate
/// the field round-trip byte-identically.
/// </param>
public sealed record ProducedOutput
{
    public string Name { get; init; } = string.Empty;
    public OutputCondition? Condition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OutputSchema Schema { get; init; }

    [JsonConstructor]
    public ProducedOutput(string Name, OutputCondition? Condition = null, OutputSchema Schema = OutputSchema.None)
    {
        if (ReservedOutputNames.IsReserved(Name))
        {
            throw new ArgumentException(
                $"ProducedOutput name '{Name}' is invalid: {ReservedOutputNames.RejectionClause}.",
                nameof(Name));
        }

        this.Name = Name ?? string.Empty;
        this.Condition = Condition;
        this.Schema = Schema;
    }
}

/// <summary>
/// The closed set of shapes a <see cref="ProducedOutput"/> can declare. Validation is
/// parse-only in every case: the engine checks the file <i>is</i> the shape, and never reads its
/// content to route (Architecture Rule 1; decision 0043's boundary).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputSchema>))]
public enum OutputSchema
{
    /// <summary>No declared shape — existence (plus any <see cref="OutputCondition"/>) suffices.</summary>
    None,

    /// <summary>The output must parse per <see cref="ReviewVerdictSchema.TryParse"/>.</summary>
    ReviewVerdict,

    /// <summary>The output must parse per <see cref="UnifiedDiffSchema.TryParse"/>.</summary>
    Diff,
}

/// <summary>
/// Extends a <see cref="ProducedOutput"/>'s contract from "this file must exist" to "this file
/// must exist and say this". Satisfied only when the file exists, parses as JSON, the
/// <paramref name="Path"/> JSON Pointer resolves, and the resolved value equals
/// <paramref name="EqualsValue"/>.
/// </summary>
/// <param name="EqualsValue">
/// Named <c>EqualsValue</c> rather than <c>Equals</c> — a record positional parameter named
/// <c>Equals</c> collides with the record's synthesized <c>Equals</c> method (CS0102). Serializes
/// under the spec's own field name, <c>equals</c>.
/// </param>
public sealed record OutputCondition(string Path, [property: JsonPropertyName("equals")] JsonScalar EqualsValue);
