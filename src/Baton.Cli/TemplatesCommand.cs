using System.Text.Json;
using Baton.Vendors;

namespace Baton.Cli;

public static class TemplatesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static Task<int> ExecuteAsync(string[] args, TextWriter stdout, CancellationToken cancellationToken = default)
    {
        bool emitJson = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

        if (emitJson)
        {
            var templates = BuiltInWorkflowTemplates.GetRoleTemplates();
            var json = JsonSerializer.Serialize(templates, JsonOptions);
            stdout.WriteLine(json);
            return Task.FromResult(0);
        }

        stdout.WriteLine("Available built-in workflow templates:");
        stdout.WriteLine();
        foreach (var info in BuiltInWorkflowTemplates.Catalog)
        {
            stdout.WriteLine($"{info.Id}");
            stdout.WriteLine($"    {info.Title}: {info.Description}");
            stdout.WriteLine();
        }

        stdout.WriteLine("Dispatch roles (machine consumers read these via --json):");
        stdout.WriteLine();
        foreach (var (id, role) in BuiltInWorkflowTemplates.GetRoleTemplates().OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            stdout.WriteLine($"{id}");
            stdout.WriteLine($"    {role.Use}");
            stdout.WriteLine();
        }

        return Task.FromResult(0);
    }
}
