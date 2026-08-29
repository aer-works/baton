using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// A scalar JSON literal — string, number, boolean, or null — used as the comparison value in an
/// <see cref="OutputCondition"/>. This is the entire condition language; it is
/// deliberately tiny and excludes composition, ranges, and regex.
/// </summary>
[JsonConverter(typeof(JsonScalarConverter))]
public abstract record JsonScalar
{
    private JsonScalar()
    {
    }

    public sealed record String(string Value) : JsonScalar;

    public sealed record Number(double Value) : JsonScalar;

    public sealed record Boolean(bool Value) : JsonScalar;

    public sealed record Null : JsonScalar
    {
        public static readonly Null Instance = new();
    }
}

internal sealed class JsonScalarConverter : JsonConverter<JsonScalar>
{
    // JsonScalar.Null is a real, non-null instance representing the JSON literal `null` — distinct
    // from a C# null, which means "no OutputCondition at all". Without opting into HandleNull,
    // System.Text.Json intercepts the JSON `null` literal before this converter ever runs and
    // produces a C# null instead, silently collapsing that distinction.
    public override bool HandleNull => true;

    public override JsonScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new JsonScalar.String(reader.GetString()!),
            JsonTokenType.Number => new JsonScalar.Number(reader.GetDouble()),
            JsonTokenType.True => new JsonScalar.Boolean(true),
            JsonTokenType.False => new JsonScalar.Boolean(false),
            JsonTokenType.Null => JsonScalar.Null.Instance,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for {nameof(JsonScalar)}."),
        };

    public override void Write(Utf8JsonWriter writer, JsonScalar? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case JsonScalar.String s:
                writer.WriteStringValue(s.Value);
                break;
            case JsonScalar.Number n:
                writer.WriteNumberValue(n.Value);
                break;
            case JsonScalar.Boolean b:
                writer.WriteBooleanValue(b.Value);
                break;
            case JsonScalar.Null or null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unknown {nameof(JsonScalar)} type {value.GetType()}.");
        }
    }
}
