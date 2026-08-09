using Aer.Adapters;
using Aer.Ui;
using Avalonia.Input;

namespace Aer.Ui.Tests;

/// <summary>
/// The permission-gate keyboard rule (0022 §4, #481), whose design citation lives on
/// <see cref="MainWindow.PermissionAnswerFor"/>: a bare <c>y</c> allows once, a bare <c>n</c> denies
/// once, and — the load-bearing negative — a reflex key can never approve. Pins that pure decision
/// without standing up a window, so "never on Enter, never with a modifier" is enforced by a check
/// that runs, not a comment.
/// </summary>
public class PermissionGateKeystrokeTests
{
    [Fact]
    public void A_bare_y_allows_once()
        => Assert.Equal(PermissionDecisionKind.AllowOnce, MainWindow.PermissionAnswerFor(Key.Y, KeyModifiers.None));

    [Fact]
    public void A_bare_n_denies_once()
        => Assert.Equal(PermissionDecisionKind.Deny, MainWindow.PermissionAnswerFor(Key.N, KeyModifiers.None));

    [Fact]
    public void Enter_never_answers_a_permission()
        => Assert.Null(MainWindow.PermissionAnswerFor(Key.Enter, KeyModifiers.None));

    [Theory]
    [InlineData(Key.Y, KeyModifiers.Control)]
    [InlineData(Key.Y, KeyModifiers.Alt)]
    [InlineData(Key.Y, KeyModifiers.Meta)]
    [InlineData(Key.N, KeyModifiers.Control)]
    [InlineData(Key.N, KeyModifiers.Shift)]
    public void A_modified_y_or_n_never_answers(Key key, KeyModifiers modifiers)
        => Assert.Null(MainWindow.PermissionAnswerFor(key, modifiers));

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Space)]
    [InlineData(Key.Escape)]
    public void Other_keys_do_not_answer(Key key)
        => Assert.Null(MainWindow.PermissionAnswerFor(key, KeyModifiers.None));
}
