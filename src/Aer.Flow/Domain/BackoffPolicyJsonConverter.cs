using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Flow.Domain;

/// <summary>
/// Custom JSON converter for <see cref="BackoffPolicy"/>.
/// Accepts preset string names ("none", "brisk", "steady", "patient"), object specifications,
/// and throws descriptive exceptions for invalid preset names.
/// </summary>
public sealed class BackoffPolicyJsonConverter : JsonConverter<BackoffPolicy>
{
    public override BackoffPolicy? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return BackoffPolicy.Default;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("Unknown Backoff preset '' for field 'Backoff'. Valid presets are: none, brisk, steady, patient.");
            }

            var trimmed = value.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
            {
                return BackoffPolicy.None;
            }
            if (string.Equals(trimmed, "brisk", StringComparison.OrdinalIgnoreCase))
            {
                return BackoffPolicy.Brisk;
            }
            if (string.Equals(trimmed, "steady", StringComparison.OrdinalIgnoreCase))
            {
                return BackoffPolicy.Steady;
            }
            if (string.Equals(trimmed, "patient", StringComparison.OrdinalIgnoreCase))
            {
                return BackoffPolicy.Patient;
            }

            throw new JsonException($"Unknown Backoff preset '{value}' for field 'Backoff'. Valid presets are: none, brisk, steady, patient.");
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            double initialMs = 0;
            double multiplier = 1;
            double maxMs = 0;
            JitterMode jitter = JitterMode.None;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return new BackoffPolicy(
                        TimeSpan.FromMilliseconds(initialMs),
                        multiplier,
                        TimeSpan.FromMilliseconds(maxMs),
                        jitter);
                }

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var propName = reader.GetString();
                    reader.Read();

                    if (string.Equals(propName, "InitialMs", StringComparison.OrdinalIgnoreCase))
                    {
                        initialMs = reader.GetDouble();
                    }
                    else if (string.Equals(propName, "Multiplier", StringComparison.OrdinalIgnoreCase))
                    {
                        multiplier = reader.GetDouble();
                    }
                    else if (string.Equals(propName, "MaxMs", StringComparison.OrdinalIgnoreCase))
                    {
                        maxMs = reader.GetDouble();
                    }
                    else if (string.Equals(propName, "Jitter", StringComparison.OrdinalIgnoreCase))
                    {
                        var jitterStr = reader.GetString();
                        if (string.Equals(jitterStr, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            jitter = JitterMode.None;
                        }
                        else if (string.Equals(jitterStr, "half", StringComparison.OrdinalIgnoreCase))
                        {
                            jitter = JitterMode.Half;
                        }
                        else
                        {
                            throw new JsonException($"Invalid Jitter mode '{jitterStr}' for field 'Backoff.Jitter'. Valid values are: none, half.");
                        }
                    }
                }
            }

            throw new JsonException("Unexpected end of JSON object when reading BackoffPolicy.");
        }

        throw new JsonException($"Unexpected JSON token '{reader.TokenType}' when deserializing BackoffPolicy.");
    }

    public override void Write(Utf8JsonWriter writer, BackoffPolicy value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value == BackoffPolicy.None)
        {
            writer.WriteStringValue("none");
        }
        else if (value == BackoffPolicy.Brisk)
        {
            writer.WriteStringValue("brisk");
        }
        else if (value == BackoffPolicy.Steady)
        {
            writer.WriteStringValue("steady");
        }
        else if (value == BackoffPolicy.Patient)
        {
            writer.WriteStringValue("patient");
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("InitialMs", value.Initial.TotalMilliseconds);
            writer.WriteNumber("Multiplier", value.Multiplier);
            writer.WriteNumber("MaxMs", value.Cap.TotalMilliseconds);
            writer.WriteString("Jitter", value.Jitter switch
            {
                JitterMode.None => "none",
                JitterMode.Half => "half",
                _ => throw new ArgumentOutOfRangeException(nameof(value.Jitter))
            });
            writer.WriteEndObject();
        }
    }
}
