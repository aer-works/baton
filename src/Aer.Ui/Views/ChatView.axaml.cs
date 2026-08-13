using System.ComponentModel;
using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Aer.Ui.Views;

/// <summary>Chat (M24 Phase 1, #262): a thin Avalonia skin over <c>MainWindowViewModel.Chat</c> — all state and daemon calls live in <c>Aer.Ui.Core</c>; button wiring stays with the shell (<c>MainWindow</c>), which owns the <c>RoomClient</c> this view's actions need.</summary>
public partial class ChatView : UserControl
{
    private ChatViewModel? _subscribedChat;

    public ChatView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedChat != null)
        {
            _subscribedChat.PropertyChanged -= OnChatPropertyChanged;
            _subscribedChat = null;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _subscribedChat = viewModel.Chat;
            _subscribedChat.PropertyChanged += OnChatPropertyChanged;
        }
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.HasPendingPermission)
            && _subscribedChat is { HasPendingPermission: true })
        {
            Dispatcher.UIThread.Post(() => ChatMessagesScroll.ScrollToEnd());
        }
    }
}
