using Baton.Dispatch;
using Baton.Domain;

namespace Baton.Vendors.Tests;

/// <summary>
/// #1373: every shipped adapter must pass the SAME string as both a spawned argument and
/// <see cref="CoreDispatchTarget.PromptText"/>.
/// <para>
/// Two mechanisms now rest on that identity, and neither of them can see a break in it:
/// <see cref="CoreDispatchTarget.WithPromptPreamble"/> finds the prompt argument by
/// <c>IndexOf(PromptText)</c> to prepend the #1373 continuation brief, and
/// <see cref="CoreDispatcher.DispatchAsync"/>'s #748 oversize swap finds it the same way to replace an
/// over-long inline prompt with a file reference. An adapter that transformed the prompt on its way
/// into argv — quoted it, folded it into <c>--prompt=…</c>, rebuilt it — would break both silently:
/// the brief would reach <c>prompt.txt</c> and never the worker, and a
/// test reading that artifact would certify it as delivered. This class is the check that fails
/// instead, per adapter, at the point the drift is introduced.
/// </para>
/// </summary>
[Collection(LaunchConfigCollection.Name)]
public class PromptArgumentInvariantTests
{
    private static readonly WorkerContract ArchitectContract = new(
        "architect", ["goal"], [new ProducedOutput("plan.md")], []);

    /// <summary>
    /// Swept from <see cref="WorkerAdapterRegistry.Default"/>, never hand-listed: a NEW adapter that
    /// sets <c>PromptText</c> is precisely the drift this class exists to catch, and a hardcoded pair
    /// is precisely what could not see it. Adapters that leave <c>PromptText</c> null carry no prose
    /// prompt at all (<c>CommandWorkerAdapter</c>'s declared argv, the no-op, the capture worker) and
    /// are filtered out here rather than asserted about — <c>WithPromptPreamble</c>'s documented no-op.
    /// </summary>
    public static TheoryData<string> ShippedPromptAdapters()
    {
        var data = new TheoryData<string>();
        foreach (var (name, adapter) in WorkerAdapterRegistry.Default)
        {
            if (TryResolve(adapter)?.PromptText is not null)
            {
                data.Add(name);
            }
        }

        Assert.NotEmpty(data);
        return data;
    }

    private static CoreDispatchTarget? TryResolve(IWorkerAdapter adapter)
    {
        try
        {
            return adapter.Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);
        }
        catch (Exception)
        {
            // An adapter that refuses this invocation shape outright (CommandWorkerAdapter demands a
            // JSON argv in its PromptTemplate) has no prose prompt to prepend to either way.
            return null;
        }
    }

    [Theory]
    [MemberData(nameof(ShippedPromptAdapters))]
    public void A_shipped_adapter_passes_its_prompt_as_an_argument_and_as_PromptText(string vendor)
    {
        var target = WorkerAdapterRegistry.Default[vendor].Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        Assert.NotNull(target.PromptText);
        Assert.True(
            target.Args.Contains(target.PromptText),
            $"'{vendor}' set PromptText but no argument equals it, so the #1373 continuation brief and "
                + "the #748 oversize swap would both silently miss the worker.");
    }

    [Theory]
    [MemberData(nameof(ShippedPromptAdapters))]
    public void A_continuation_brief_reaches_a_shipped_adapters_spawned_argument(string vendor)
    {
        const string brief = "[baton] CONTINUATION BRIEF -- finish, do not restart.\n\n";
        var target = WorkerAdapterRegistry.Default[vendor].Resolve(new WorkerInvocation("Draft a plan."), ArchitectContract);

        var prefixed = target.WithPromptPreamble(brief);

        // End to end for this half: not "the method returns a prefixed string" (CoreDispatcherTests
        // pins that against a synthetic target) but "the real vendor argv the worker is spawned with
        // now starts with the brief".
        Assert.True(
            prefixed.Args.Any(arg => arg.StartsWith(brief, StringComparison.Ordinal)),
            $"'{vendor}' spawns no argument carrying the continuation brief.");
        Assert.StartsWith(brief, prefixed.PromptText!, StringComparison.Ordinal);
    }
}
