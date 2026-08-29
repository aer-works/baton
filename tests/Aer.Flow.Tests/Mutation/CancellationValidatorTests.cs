using Aer.Flow.Domain;
using Aer.Flow.Mutation;

namespace Aer.Flow.Tests.Mutation;

/// <summary>
/// M10 Phase 1: the validation matrix for <see cref="CancellationValidator.Validate"/> — a
/// pure function, no I/O — mirroring <see cref="ExternalDecisionValidatorTests"/>'s style.
/// </summary>
public class CancellationValidatorTests
{
    private static readonly ExecutionId Known = new("exec-1");
    private static readonly ExecutionId Unknown = new("exec-unknown");

    private static readonly IReadOnlySet<ExecutionId> KnownSet = new HashSet<ExecutionId> { Known };

    [Fact]
    public void A_known_ExecutionId_is_valid()
    {
        var exception = Record.Exception(() => CancellationValidator.Validate(KnownSet, Known));

        Assert.Null(exception);
    }

    [Fact]
    public void A_known_but_already_terminal_ExecutionId_is_still_valid()
    {
        // The too-late request is recorded, not rejected — validity here has nothing to
        // do with whether the target has already reached a terminal outcome.
        var exception = Record.Exception(() => CancellationValidator.Validate(KnownSet, Known));

        Assert.Null(exception);
    }

    [Fact]
    public void An_unknown_ExecutionId_throws()
    {
        Assert.Throws<UnknownExecutionIdException>(() => CancellationValidator.Validate(KnownSet, Unknown));
    }

    [Fact]
    public void An_empty_known_set_throws_for_any_target()
    {
        Assert.Throws<UnknownExecutionIdException>(() => CancellationValidator.Validate(new HashSet<ExecutionId>(), Known));
    }
}
