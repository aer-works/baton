using System.Text.Json;
using Baton.Domain;

namespace Baton.Templates;

/// <summary>
/// Writes a <see cref="WorkflowDefinition"/> template to a file — the first template
/// write path anywhere in the stack (M16 Phase 1, issue #150), and the counterpart to
/// <see cref="WorkflowDefinitionParser"/>.
/// <para>
/// <b>Placement decision of record:</b> the writer lives here, beside its parser in
/// <c>Baton.Templates</c>, not in a caller's assembly (the desktop app that originally owned
/// this write is deleted, #1412). Round-trip fidelity (save → parse → validate through the exact code
/// every other consumer uses) is a domain-layer property, guaranteed by construction only when
/// serialization and deserialization share the same <see cref="JsonSerializer"/> converters; and
/// <see cref="SnapshotBinder.PersistAsync"/> already established that file-writing helpers live in
/// this namespace when what they persist is a domain record. Flow's engine still never writes a
/// template on any execution path — this type has no caller inside <c>Baton</c> itself; today's
/// callers are DispatchCommand and the built-in templates (any future authoring surface joins them).
/// </para>
/// <para>
/// Output is indented: a template is a human-editable file (the spec explicitly contemplates "a
/// human editing the file by hand"), so the saved form matches the hand-authored fixtures already
/// in the repo rather than <see cref="SnapshotBinder"/>'s compact machine-only form. The
/// round-trip bar is parse-level fidelity through the shared converters, never byte-level.
/// </para>
/// </summary>
public static class WorkflowDefinitionWriter
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>Serializes <paramref name="definition"/> as indented template JSON, validating it first.</summary>
    /// <exception cref="WorkflowDefinitionValidationException">
    /// <paramref name="definition"/> fails structural validation (see
    /// <see cref="WorkflowDefinitionValidator.Validate"/>). Validated here for the same reason
    /// <see cref="SnapshotBinder.Bind"/> re-validates: this is a public entry point of its own,
    /// and a saved template file must always be engine-valid — every reader goes through
    /// <see cref="WorkflowDefinitionParser"/>, which would reject it anyway; failing at write time
    /// keeps the invalid state out of the file instead of discovering it on the next open.
    /// (Whether a structurally invalid in-progress graph may be saved as a draft is M16 Phase 2's
    /// named open question; until it decides otherwise, save-validity holds.)
    /// </exception>
    public static string Serialize(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);
        return JsonSerializer.Serialize(definition, IndentedOptions);
    }

    /// <summary>
    /// Persists <paramref name="definition"/> as template JSON at <paramref name="templateFilePath"/>,
    /// creating parent directories as needed — the same shape as <see cref="SnapshotBinder.PersistAsync"/>.
    /// </summary>
    /// <exception cref="WorkflowDefinitionValidationException">
    /// <paramref name="definition"/> fails structural validation; nothing is written.
    /// </exception>
    public static async Task SaveToFileAsync(
        WorkflowDefinition definition,
        string templateFilePath,
        CancellationToken cancellationToken = default)
    {
        var json = Serialize(definition);

        var directory = Path.GetDirectoryName(templateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(templateFilePath, json, cancellationToken).ConfigureAwait(false);
    }
}
