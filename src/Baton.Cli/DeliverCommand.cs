using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Baton.Status;
using Baton.Vendors;

namespace Baton.Cli;

public sealed record DeliverResult(
    string Title,
    string SourcePath,
    string DestinationPath,
    string Sha256,
    string DeliveredAt);

public sealed record ConductorManifestEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("delivered_at")] string DeliveredAt,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// <c>baton deliver</c> (#1669): copies a conductor deliverable into a room's artifacts directory
/// with a manifest entry so pusher.py forwards it to the glass inbox.
/// </summary>
public static class DeliverCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static async Task<DeliverResult> ExecuteAsync(
        DeliverOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        if (!File.Exists(options.SourceFilePath))
        {
            throw new CliArgumentException($"Source file '{options.SourceFilePath}' does not exist.");
        }

        var sourceFullPath = Path.GetFullPath(options.SourceFilePath);
        var basename = Path.GetFileName(sourceFullPath);

        var roomDir = options.RoomDirectoryPath;
        var conductorArtifactsDir = Path.Combine(roomDir, "artifacts", "conductor");
        Directory.CreateDirectory(conductorArtifactsDir);

        var bindingsPath = BatonPaths.RoomBindingsFile(roomDir);
        if (!File.Exists(bindingsPath))
        {
            const string stubBindings = """
                {
                  "conductor": {
                    "Adapter": "none",
                    "Contract": {
                      "WorkerName": "conductor"
                    },
                    "PromptTemplate": "conductor",
                    "Timeout": "01:00:00"
                  }
                }
                """;
            File.WriteAllText(bindingsPath, stubBindings, Encoding.UTF8);
        }

        await RoomRegistryStore.AppendAsync(
            roomDir,
            BatonPaths.Root,
            BatonPaths.RoomRegistryFile,
            explicitRegister: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var destFilePath = Path.Combine(conductorArtifactsDir, basename);
        File.Copy(sourceFullPath, destFilePath, overwrite: true);

        var fileBytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken).ConfigureAwait(false);
        var sha256Hex = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

        string title;
        if (!string.IsNullOrWhiteSpace(options.Title))
        {
            title = options.Title;
        }
        else
        {
            var text = Encoding.UTF8.GetString(fileBytes);
            title = ExtractTitle(text, basename);
        }

        var deliveredAt = DateTime.UtcNow.ToString("O");
        var entry = new ConductorManifestEntry(title, sourceFullPath, deliveredAt, sha256Hex);

        var manifestPath = Path.Combine(conductorArtifactsDir, "manifest.jsonl");
        UpdateManifest(manifestPath, entry);

        output.WriteLine($"Delivered '{title}' -> {destFilePath}");
        return new DeliverResult(title, sourceFullPath, destFilePath, sha256Hex, deliveredAt);
    }

    private static string ExtractTitle(string content, string fallback)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ") && trimmed.Length > 2)
            {
                return trimmed[2..].Trim();
            }
        }

        return fallback;
    }

    private static void UpdateManifest(string manifestPath, ConductorManifestEntry entry)
    {
        var entries = new List<ConductorManifestEntry>();
        var replaced = false;

        if (File.Exists(manifestPath))
        {
            var lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var existing = JsonSerializer.Deserialize<ConductorManifestEntry>(line, JsonOptions);
                    if (existing is null || string.IsNullOrWhiteSpace(existing.SourcePath))
                    {
                        continue;
                    }

                    if (string.Equals(existing.SourcePath, entry.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(entry);
                        replaced = true;
                    }
                    else
                    {
                        entries.Add(existing);
                    }
                }
                catch (JsonException)
                {
                    // Skip corrupt lines
                }
            }
        }

        if (!replaced)
        {
            entries.Add(entry);
        }

        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append(JsonSerializer.Serialize(e, JsonOptions)).Append('\n');
        }

        var tempPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, sb.ToString(), Encoding.UTF8);
        File.Move(tempPath, manifestPath, overwrite: true);
    }
}
