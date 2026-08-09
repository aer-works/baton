using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Input;

namespace Aer.Ui.Views;

/// <summary>Chat (M24 Phase 1, #262): a thin Avalonia skin over <c>MainWindowViewModel.Chat</c> — all state and daemon calls live in <c>Aer.Ui.Core</c>; button wiring stays with the shell (<c>MainWindow</c>), which owns the <c>RoomClient</c> this view's actions need.</summary>
public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        // #981: the upward half of the adapter combo's deliberately-severed two-way binding (see
        // the axaml comment). Null is ignored on purpose — the control clears its selection every
        // time PopulateAvailableAdapters refreshes ItemsSource, and writing that null into the
        // ViewModel is precisely the defect; only a person's real pick travels up.
        ChatNewAdapterCombo.SelectionChanged += (_, _) =>
        {
            if (ChatNewAdapterCombo.SelectedItem is string adapter
                && DataContext is MainWindowViewModel viewModel
                && !string.Equals(viewModel.Chat.NewChatAdapter, adapter, StringComparison.Ordinal))
            {
                viewModel.Chat.NewChatAdapter = adapter;
            }
        };

        // 0022 §4 / #481: y answers "Allow once" and n answers "Deny once" on a pending permission —
        // and neither is ever bound to Enter (Enter still sends, via MainWindow.OnChatInputBoxKeyDown).
        // Handled at the view root, and only when no text field is focused, so typing y/n into the
        // composer never answers the gate by accident — "a gate clearable by muscle memory is
        // decorative".
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Y or Key.N)
            || DataContext is not MainWindowViewModel { Chat.PendingPermission: { } gate })
        {
            return;
        }

        // Never steal the keystroke from a focused text field — the operator may be typing a reply.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        var command = e.Key == Key.Y ? gate.AllowOnceCommand : gate.DenyCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }
}
