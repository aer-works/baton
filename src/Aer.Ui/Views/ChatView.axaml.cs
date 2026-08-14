using System.ComponentModel;
using Aer.Ui.Core;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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

    /// <summary>
    /// The feedback-file picker, moved here with the decision card it belongs to (#1196 slice 3) —
    /// a real OS file dialog writing into <see cref="PausedStepViewModel.RevisionFilePath"/>, the
    /// same property the visible text box binds (still swappable by hand, and what headless tests
    /// set directly — a dialog cannot be driven headlessly).
    /// </summary>
    private async void OnChooseFeedbackFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not PausedStepViewModel pausedStep ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose the feedback file",
            AllowMultiple = false,
        });

        if (files.Count == 1 && files[0].TryGetLocalPath() is { } localPath)
        {
            pausedStep.RevisionFilePath = localPath;
        }
    }

    private async void OnCopyFailureClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ChatMessageViewModel message)
        {
            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel?.Clipboard is { } clipboard)
            {
                // #1180: the out-of-plan card's Text is the plain-language 0026 sentence, but its
                // Copy carries the vendor's raw words (ChatMessageViewModel.CopyText) instead --
                // shared with the failure card's own Copy button, which never sets CopyText and so
                // keeps copying Text unchanged.
                await clipboard.SetTextAsync(message.CopyText ?? message.Text);
            }
        }
    }
}
