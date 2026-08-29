using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// An environment variable declared on an <see cref="ExecutionRequest"/>. AER-computed
/// variables (<c>AER_INPUT_&lt;n&gt;</c>, <c>AER_OUTPUT_DIR</c>) are derived paths and are
/// recorded in full. Pass-through variables (API keys, tokens, vendor settings sourced from the
/// invoking environment) are recorded by name only, never by value — their values are resolved and
/// injected at dispatch time, immediately before submission to Core, and never touch the Event Store.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AerComputed), "aerComputed")]
[JsonDerivedType(typeof(PassThrough), "passThrough")]
public abstract record EnvironmentVariable(string Name)
{
    public sealed record AerComputed(string Name, string Value) : EnvironmentVariable(Name);

    public sealed record PassThrough(string Name) : EnvironmentVariable(Name);
}
