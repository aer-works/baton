using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Domain;

namespace Baton.Store;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> every <see cref="WorkflowDefinitionSnapshot"/> and
/// <see cref="WorkflowDefinition"/> is written and read with (#619). Shared rather than defaulted
/// per call site, because a snapshot written under one configuration and read under another is not
/// a round trip.
/// </summary>
/// <remarks>
/// <para>
/// <b>Enums persist by name, not by ordinal.</b> The full enum population reachable from
/// <see cref="WorkflowDefinitionSnapshot"/> / <see cref="WorkflowDefinition"/>:
/// <list type="bullet">
/// <item><see cref="PausePointKind"/></item>
/// <item><see cref="JitterMode"/></item>
/// </list>
/// Without <see cref="JsonStringEnumConverter"/>, <see cref="PausePointKind.NeedsInput"/> is stored as
/// <c>1</c> and <see cref="JitterMode.Half"/> as <c>1</c>. Storing by name prevents declaration-order
/// reinterpretation on disk.
/// </para>
/// <para>
/// <b>Reading accepts legacy ordinals as well as names.</b> <see cref="JsonStringEnumConverter"/> accepts
/// numbers as well as names, ensuring legacy snapshots written before #619 with ordinal enums keep reading
/// back correctly without requiring data migrations.
/// </para>
/// <para>
/// <b>Constructor parameter enforcement.</b>
/// <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/> ensures required constructor parameters (such as WorkflowTemplateId or Steps) cannot be silently defaulted to null/empty when deserializing snapshots or template definitions.
/// </para>
/// </remarks>
public static class SnapshotJson
{
    /// <summary>The snapshot's wire contract. Never construct a second one — see this type's remarks.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>
    /// The template's read contract (#619): the same by-name enums, deliberately WITHOUT
    /// <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/>.
    /// </summary>
    /// <remarks>
    /// The two files are different contracts, exactly as #619 warned. A template is human-authored
    /// input: a missing member must reach <see cref="Templates.WorkflowDefinitionValidator"/>'s
    /// structural rejection — a named property in an actionable message, the behaviour
    /// <c>WorkflowDefinitionParserTests</c>' missing-member arms pin — rather than die inside the
    /// serializer as a raw missing-parameter error (#562 is open about exactly that reading
    /// experience). <c>snapshot.json</c> is the opposite contract: machine-written durable state,
    /// where a silently defaulted member IS the corruption and throwing early is the point.
    /// </remarks>
    public static JsonSerializerOptions TemplateOptions { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
