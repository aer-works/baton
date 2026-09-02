using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baton.Domain;

/// <summary>
/// Custom JSON converter for <see cref="EnvironmentVariable"/> polymorphic serialization and deserialization.
/// Emits <c>batonComputed</c> and <c>passThrough</c> on write, while accepting legacy <c>aerComputed</c>
/// discriminators on read for backwards compatibility with pre-rename journals (#1580).
/// </summary>
public sealed class EnvironmentVariableJsonConverter : JsonConverter<EnvironmentVariable>
{
    public override bool CanConvert(Type typeToConvert) =>
        typeof(EnvironmentVariable).IsAssignableFrom(typeToConvert);

    public override EnvironmentVariable? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Unexpected token '{reader.TokenType}' when deserializing EnvironmentVariable.");
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string? kind = null;
        string? name = null;
        string? value = null;

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "kind", StringComparison.OrdinalIgnoreCase))
            {
                kind = RequireString(prop);
            }
            else if (string.Equals(prop.Name, "Name", StringComparison.OrdinalIgnoreCase))
            {
                name = RequireString(prop);
            }
            else if (string.Equals(prop.Name, "Value", StringComparison.OrdinalIgnoreCase))
            {
                value = RequireString(prop);
            }
        }

        if (kind is null)
        {
            throw new JsonException("Missing required type discriminator 'kind' for EnvironmentVariable.");
        }

        EnvironmentVariable result;
        if (string.Equals(kind, "batonComputed", StringComparison.Ordinal) ||
            string.Equals(kind, "aerComputed", StringComparison.Ordinal))
        {
            if (name is null)
            {
                throw new JsonException($"Missing required property 'Name' for '{kind}'.");
            }
            if (value is null)
            {
                throw new JsonException($"Missing required property 'Value' for '{kind}'.");
            }

            result = new EnvironmentVariable.BatonComputed(name, value);
        }
        else if (string.Equals(kind, "passThrough", StringComparison.Ordinal))
        {
            if (name is null)
            {
                throw new JsonException("Missing required property 'Name' for 'passThrough'.");
            }

            result = new EnvironmentVariable.PassThrough(name);
        }
        else
        {
            throw new JsonException($"Read unrecognized type discriminator id '{kind}'.");
        }

        if (typeToConvert != typeof(EnvironmentVariable) && !typeToConvert.IsInstanceOfType(result))
        {
            throw new JsonException($"Cannot convert '{result.GetType().Name}' to requested type '{typeToConvert.Name}'.");
        }

        return result;
    }

    private static string? RequireString(JsonProperty prop)
    {
        if (prop.Value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            throw new JsonException($"'{prop.Name}' must be a string on EnvironmentVariable.");
        }

        return prop.Value.GetString();
    }

    public override void Write(Utf8JsonWriter writer, EnvironmentVariable value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        switch (value)
        {
            case EnvironmentVariable.BatonComputed computed:
                writer.WriteString("kind", "batonComputed");
                writer.WriteString("Name", computed.Name);
                writer.WriteString("Value", computed.Value);
                break;
            case EnvironmentVariable.PassThrough passThrough:
                writer.WriteString("kind", "passThrough");
                writer.WriteString("Name", passThrough.Name);
                break;
            default:
                throw new JsonException($"Unknown EnvironmentVariable type '{value.GetType().FullName}'.");
        }
        writer.WriteEndObject();
    }
}
