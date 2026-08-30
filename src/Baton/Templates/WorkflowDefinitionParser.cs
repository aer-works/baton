using System.Text.Json;
using Baton.Domain;
using Baton.Store;

namespace Baton.Templates;

/// <summary>
/// Loads a <see cref="WorkflowDefinition"/> template from a file and validates it.
/// <para>
/// <b>File format convention:</b> templates are plain JSON, deserialized through the same
/// <see cref="JsonSerializer"/> converters the rest of <c>Baton</c> already uses for
/// <c>flow.jsonl</c> and every other domain record. The spec leaves the format
/// implementation-defined; JSON was chosen over TOML specifically to avoid a second
/// serialization stack for one file type, not because a template is itself a JSON Lines stream —
/// a template is a single document, so it is <c>.json</c>, not <c>.jsonl</c>.
/// </para>
/// </summary>
public static class WorkflowDefinitionParser
{
    /// <summary>Parses and validates a template from a JSON string.</summary>
    /// <param name="json">The template document.</param>
    /// <param name="sourcePath">
    /// The file <paramref name="json"/> was read from, named in the error when the JSON is
    /// malformed (#562) — <c>null</c> for callers with no file, e.g. an in-memory template.
    /// </param>
    /// <exception cref="WorkflowDefinitionValidationException">
    /// The JSON is malformed, empty, or the parsed <see cref="WorkflowDefinition"/> fails
    /// structural validation (see <see cref="WorkflowDefinitionValidator.Validate"/>).
    /// </exception>
    public static WorkflowDefinition Parse(string json, string? sourcePath = null)
    {
        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<WorkflowDefinition>(json, SnapshotJson.TemplateOptions);
        }
        catch (JsonException ex)
        {
            var message = IsConverterErrorMessage(ex.Message)
                ? ex.Message
                : BuildMalformedJsonMessage(ex, sourcePath);
            throw new WorkflowDefinitionValidationException([message], ex);
        }

        if (definition is null)
        {
            var location = sourcePath is null ? string.Empty : $" '{sourcePath}'";
            throw new WorkflowDefinitionValidationException([$"Template file{location} did not contain a WorkflowDefinition object."]);
        }

        WorkflowDefinitionValidator.Validate(definition);
        return definition;
    }

    /// <summary>Reads <paramref name="path"/> and parses it as a <see cref="WorkflowDefinition"/> template.</summary>
    /// <exception cref="WorkflowDefinitionValidationException">
    /// The file does not exist (or its directory does not), or its contents are malformed/invalid.
    /// A missing file is translated here rather than left as a raw <see cref="FileNotFoundException"/>:
    /// that BCL type is not an <c>BatonFlowException</c>, so an unwrapped one escapes the CLI's typed
    /// boundary as a crash — the same class this loader's malformed-JSON handling already covers.
    /// </exception>
    public static async Task<WorkflowDefinition> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new WorkflowDefinitionValidationException([$"Template file '{path}' does not exist."], ex)
            {
                TryInvocation = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "'baton run' takes a workflow FILE; built-in templates are used via 'baton dispatch <role>'.",
            };
        }

        return Parse(json, path);
    }

    /// <summary>
    /// Builds a <see cref="WorkflowDefinitionValidationException"/> message for a malformed
    /// template (#562): the source file (when known), the raw <see cref="JsonException"/> (never
    /// swallowed — it stays the inner exception too), and a hint at the shape the author should
    /// have written, since <see cref="JsonException.Message"/> alone names the .NET type and JSON
    /// path but never what a valid document looks like.
    /// </summary>
    private static string BuildMalformedJsonMessage(JsonException ex, string? sourcePath)
    {
        var location = sourcePath is null ? string.Empty : $" in '{sourcePath}'";
        return $"Malformed template JSON{location}: {ex.Message} {ExplainShapeHint(ex.Path)}";
    }

    /// <summary>
    /// Maps a <see cref="JsonException.Path"/> to a plain-language description of the shape that
    /// path expects, for the mistakes #562 reported hand-authoring a template against
    /// <c>tests/Baton.Cli.SmokeTests/Fixtures/draft-review-workflow.json</c>: a quoted
    /// <c>WorkflowTemplateVersion</c> ("1.0.0" instead of 1), and an object <c>Inputs</c>/
    /// <c>Outputs</c>/<c>DependsOn</c> ({} instead of []). Falls back to the whole-document shape
    /// for any path not covered — the field-level mistake is not known, but the correct shape
    /// still is.
    /// </summary>
    private static string ExplainShapeHint(string? jsonPath)
    {
        const string wholeDocumentShape =
            "A valid WorkflowDefinition looks like: "
            + "{ \"WorkflowTemplateId\": \"<string>\", \"WorkflowTemplateVersion\": <integer>, "
            + "\"Steps\": [ { \"StepId\": \"<string>\", \"Worker\": \"<string>\", \"Inputs\": [<strings>], "
            + "\"Outputs\": [<strings>], \"DependsOn\": [<StepId strings>], \"RetryPolicy\": { \"MaxAttempts\": <integer> } } ] }.";

        if (jsonPath is null)
        {
            return wholeDocumentShape;
        }

        if (jsonPath == "$.WorkflowTemplateVersion")
        {
            return "Expected a plain integer (e.g. \"WorkflowTemplateVersion\": 1), not a quoted string.";
        }

        if (jsonPath.EndsWith(".Inputs", StringComparison.Ordinal)
            || jsonPath.EndsWith(".Outputs", StringComparison.Ordinal)
            || jsonPath.EndsWith(".DependsOn", StringComparison.Ordinal))
        {
            return $"Expected a JSON array of strings at {jsonPath} (e.g. [] or [\"draft\"]), not an object.";
        }

        return wholeDocumentShape;
    }

    private static readonly string[] ConverterMessagePrefixes =
    [
        "Unknown Backoff preset",
        "Invalid Jitter mode",
        "Unexpected end of JSON object when reading BackoffPolicy",
        "Unexpected JSON token",
        "Expected a string for",
        "Expected a string property name for"
    ];

    private static bool IsConverterErrorMessage(string message)
    {
        foreach (var prefix in ConverterMessagePrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
