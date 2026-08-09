using Aer.Adapters;
using Aer.Flow.Projection;
using Aer.Ui.Core;
using Xunit;

namespace Aer.Ui.Tests;

public class PendingPermissionViewModelTests
{
    private static PendingPermission ShellAsk(string command = "rm -rf build/", string requestId = "req-1") =>
        new(requestId, "chat-worker", "claude", "Bash", $"{{\"command\":\"{command}\"}}", "shell", DateTimeOffset.UtcNow);

    private static PendingPermission NonShellAsk(string requestId = "req-1") =>
        new(requestId, "chat-worker", "claude", "Edit", "{\"file_path\":\"/tmp/x\"}", "write", DateTimeOffset.UtcNow);

    private static (PendingPermissionViewModel Vm, List<(string Id, string Kind, string? Reason)> Answers) Build(PendingPermission ask)
    {
        var answers = new List<(string, string, string?)>();
        var vm = new PendingPermissionViewModel(ask, (id, kind, reason) =>
        {
            answers.Add((id, kind, reason));
            return Task.CompletedTask;
        });
        return (vm, answers);
    }

    [Fact]
    public void ShellAsk_DerivesCommandFamily_AndShowsCommandLine()
    {
        var (vm, _) = Build(ShellAsk());

        Assert.Equal("rm", vm.CommandFamily);
        Assert.True(vm.HasCommandScope);
        Assert.Contains("rm -rf build/", vm.PromptText);
        Assert.Equal("Allow rm in this room", vm.AllowCommandInRoomLabel);
        Assert.Equal("Always deny rm", vm.DenyAlwaysLabel);
    }

    [Fact]
    public void NonShellAsk_HasNoCommandScope_AndNamesTheTool()
    {
        var (vm, _) = Build(NonShellAsk());

        Assert.Null(vm.CommandFamily);
        Assert.False(vm.HasCommandScope);
        Assert.Contains("Edit", vm.PromptText);
    }

    [Fact]
    public void MetacharacterHeadCommand_FailsClosed_NoCommandScope()
    {
        // A command opening with a shell metacharacter cannot be scoped safely (the amender's own
        // fail-closed) — the command-family rungs must not be offered rather than silently narrowed.
        var (vm, _) = Build(ShellAsk(command: "$(whoami)"));

        Assert.Null(vm.CommandFamily);
        Assert.False(vm.HasCommandScope);
    }

    [Theory]
    [InlineData(PermissionDecisionKind.AllowOnce)]
    [InlineData(PermissionDecisionKind.AllowCommandInRoom)]
    [InlineData(PermissionDecisionKind.AllowRoom)]
    [InlineData(PermissionDecisionKind.Deny)]
    [InlineData(PermissionDecisionKind.DenyAlways)]
    public async Task EachRung_AnswersWithItsOwnDecisionKind_ForThisRequest(string expectedKind)
    {
        var (vm, answers) = Build(ShellAsk(requestId: "req-42"));

        var command = expectedKind switch
        {
            PermissionDecisionKind.AllowOnce => vm.AllowOnceCommand,
            PermissionDecisionKind.AllowCommandInRoom => vm.AllowCommandInRoomCommand,
            PermissionDecisionKind.AllowRoom => vm.AllowRoomCommand,
            PermissionDecisionKind.Deny => vm.DenyCommand,
            PermissionDecisionKind.DenyAlways => vm.DenyAlwaysCommand,
            _ => throw new InvalidOperationException(),
        };
        await command.ExecuteAsync(null);

        var answer = Assert.Single(answers);
        Assert.Equal("req-42", answer.Id);
        Assert.Equal(expectedKind, answer.Kind);
    }

    [Fact]
    public void CrossRoomRung_IsNotOffered_HeldByDecision0052()
    {
        // The ladder ships without "any this command in any room" (0052). There is no command for it,
        // so no operator can answer with a scope the engine would silently drop to once-only. This is a
        // structural assertion: if a future edit adds an AllowCommandAnyRoom command, it must be a
        // deliberate reopening of 0052, not an accident this test lets through.
        var kinds = typeof(PendingPermissionViewModel)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n.EndsWith("Command", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain("AllowCommandAnyRoomCommand", kinds);
    }

    [Fact]
    public void Disabled_GatesEveryRung()
    {
        var (vm, _) = Build(ShellAsk());

        vm.IsEnabled = false;

        Assert.False(vm.AllowOnceCommand.CanExecute(null));
        Assert.False(vm.AllowCommandInRoomCommand.CanExecute(null));
        Assert.False(vm.AllowRoomCommand.CanExecute(null));
        Assert.False(vm.DenyCommand.CanExecute(null));
        Assert.False(vm.DenyAlwaysCommand.CanExecute(null));

        vm.IsEnabled = true;
        Assert.True(vm.AllowOnceCommand.CanExecute(null));
    }

    [Fact]
    public void AllowRoomDisclosure_NamesWhatTheShellReaches()
    {
        var (vm, _) = Build(ShellAsk());

        // The honesty clause under the room ceiling must name files and the network, so an operator is
        // not told "any command in this room" without being told what a command reaches.
        Assert.Contains("read files", vm.AllowRoomDisclosure);
        Assert.Contains("write files", vm.AllowRoomDisclosure);
        Assert.Contains("network access", vm.AllowRoomDisclosure);
    }
}
