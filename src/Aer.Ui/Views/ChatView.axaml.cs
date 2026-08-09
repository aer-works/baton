using Aer.Ui.Core;
using Avalonia.Controls;

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
    }
}
