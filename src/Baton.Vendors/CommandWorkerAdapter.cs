using System.Text.Json;
using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors;

/// <summary>
/// Deterministic worker adapter that executes a declared command without shell interpretation
/// (Architecture Rule 1: the argv comes from structured workflow config, never from AI output, and
/// no shell ever parses it). The argv rides in <see cref="WorkerInvocation.PromptTemplate"/> as a
/// JSON string array — the same field <see cref="CaptureWorkerAdapter"/> already repurposes for
/// non-prose per-step data. A bare
/// non-array value is treated as a single-element argv (just the executable).
/// <para>
/// <b>Stdout becomes the FIRST declared output</b> in <see cref="WorkerContract.ProducedOutputs"/>
/// (a process has one stdout, so one artifact can be fed by it); any further declared outputs must
/// be written by the command itself, and <c>ContractValidator</c> fails the execution if they are
/// not. No declared outputs → stdout goes only to the execution stream logs.
/// </para>
/// <para>
/// <b>No grant machinery</b>: this adapter is engine-deterministic, not a vendor worker — there is
/// no AI on the other end to grant anything to. Contrast <see cref="CaptureWorkerAdapter"/>, the
/// fixed-purpose git-diff step this generalizes alongside (not a replacement: capture stays the
/// composer's named capability).
/// </para>
/// </summary>
public sealed class CommandWorkerAdapter : IWorkerAdapter
{
    public const string AdapterName = "command";

    public CoreDispatchTarget Resolve(WorkerInvocation invocation, WorkerContract contract)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(contract);

        if (string.IsNullOrWhiteSpace(invocation.PromptTemplate))
        {
            throw new InvalidOperationException(
                "CommandWorkerAdapter requires PromptTemplate to contain a JSON string array of argv elements or executable name.");
        }

        List<string>? argv;
        var trimmed = invocation.PromptTemplate.TrimStart();
        if (trimmed.StartsWith("["))
        {
            try
            {
                argv = JsonSerializer.Deserialize<List<string>>(invocation.PromptTemplate);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"CommandWorkerAdapter failed to parse PromptTemplate as a JSON string array: {ex.Message}", ex);
            }
        }
        else
        {
            argv = [invocation.PromptTemplate];
        }

        if (argv == null || argv.Count == 0)
        {
            throw new InvalidOperationException("CommandWorkerAdapter argv list cannot be empty.");
        }

        if (argv.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "CommandWorkerAdapter argv list contains a null or blank element -- every element "
                + "must be a non-empty string (a JSON null or \"\" in the declared argv is a "
                + "workflow-config defect, refused here rather than passed to process creation).");
        }

        var program = argv[0];
        var args = argv.Skip(1).ToList();

        var outputArtifactName = contract.ProducedOutputs.Count > 0 ? contract.ProducedOutputs[0].Name : null;

        return new CoreDispatchTarget(
            Program: program,
            Args: args,
            WorkingDirectory: invocation.WorkingDirectory,
            StdoutArtifactName: outputArtifactName);
    }
}
