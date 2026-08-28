using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aer.Adapters;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Aer.Ui.Views;

public partial class TemplatePickerWindow : Window
{
    private readonly MainWindow? _owner;

    public string? MaterializedRoomDirectoryPath { get; private set; }

    /// <summary>
    /// <paramref name="owner"/> routes chat/codebase session creation through
    /// <see cref="MainWindow.StartInteractiveSessionAsync"/> (M24 Phase 1 desktop wiring, issue #262)
    /// instead of materializing directly in-process -- null only in the pre-existing edge case where
    /// <see cref="HomeView.OnStartTemplateClick"/> could not resolve a <see cref="MainWindow"/> owner,
    /// in which case those two template kinds fall back to the direct materialization this window
    /// always used before.
    /// </summary>
    public TemplatePickerWindow(MainWindow? owner)
    {
        _owner = owner;
        InitializeComponent();
        PopulateVendors();
    }

    /// <summary>Parameterless overload Avalonia's XAML resource loader requires (AVLN3001) -- design-time/preview tooling only; real callers use <see cref="TemplatePickerWindow(MainWindow?)"/>.</summary>
    public TemplatePickerWindow() : this(null)
    {
    }

    private void PopulateVendors()
    {
        var probed = VendorCliPresence.Probe();
        // AdapterName, not BinaryName — ChatViewModel.PopulateAvailableAdapters says why.
        var available = probed.Where(p => p.IsAvailable).Select(p => p.AdapterName).ToList();
        if (available.Count == 0)
        {
            available = ["claude", "agy"]; // vocabulary-ok: adapter contract keys, not display text
        }

        PrimaryVendorCombo.ItemsSource = available;
        PrimaryVendorCombo.SelectedIndex = 0;

        SecondaryVendorCombo.ItemsSource = available;
        SecondaryVendorCombo.SelectedIndex = available.Count > 1 ? 1 : 0;
    }

    private void OnTemplateChanged(object? sender, RoutedEventArgs e)
    {
        if (SecondaryVendorPanel != null)
        {
            SecondaryVendorPanel.IsVisible = ReviewRunRadio.IsChecked == true;
        }
        if (ProjectDirectoryPanel != null)
        {
            ProjectDirectoryPanel.IsVisible = CodebaseSessionRadio.IsChecked == true;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        string templateId;
        if (ChatSessionRadio.IsChecked == true) templateId = "chat-session"; // vocabulary-ok: template id key
        else if (CodebaseSessionRadio.IsChecked == true) templateId = "codebase-session"; // vocabulary-ok: template id key
        else if (ReviewRunRadio.IsChecked == true) templateId = "review-run";
        else templateId = "solo-run";

        var primaryVendor = PrimaryVendorCombo.SelectedItem?.ToString() ?? "claude";
        var secondaryVendor = ReviewRunRadio.IsChecked == true
            ? (SecondaryVendorCombo.SelectedItem?.ToString() ?? primaryVendor)
            : null;
        var roomName = string.IsNullOrWhiteSpace(RoomNameBox.Text) ? $"room-{DateTime.UtcNow:yyyyMMddHHmmss}" : RoomNameBox.Text.Trim();
        var customPrompt = string.IsNullOrWhiteSpace(CustomPromptBox.Text) ? null : CustomPromptBox.Text.Trim();
        var secondaryCustomPrompt = string.IsNullOrWhiteSpace(SecondaryCustomPromptBox.Text) ? null : SecondaryCustomPromptBox.Text.Trim();

        var baseRoomsDir = AerPaths.Rooms;
        var roomDirectoryPath = Path.GetFullPath(Path.Combine(baseRoomsDir, roomName));
        if (!roomDirectoryPath.StartsWith(Path.GetFullPath(baseRoomsDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Close(false);
            return;
        }

        try
        {
            if (templateId == "chat-session" || templateId == "codebase-session") // vocabulary-ok: template id key
            {
                var workDir = CodebaseSessionRadio.IsChecked == true && !string.IsNullOrWhiteSpace(ProjectDirectoryBox.Text)
                    ? ProjectDirectoryBox.Text.Trim()
                    : null;

                if (_owner != null)
                {
                    var request = new StartSessionRequest(
                        Adapter: primaryVendor,
                        RoomName: roomName,
                        WorkingDirectory: workDir,
                        InitialMessage: customPrompt);

                    var outcome = await _owner.StartInteractiveSessionAsync(request).ConfigureAwait(true);
                    if (outcome.Metadata is null)
                    {
                        ShowError(outcome.ErrorMessage ?? "Failed to start the room.");
                        return;
                    }

                    roomDirectoryPath = outcome.Metadata.RoomDirectoryPath;
                }
                else
                {
                    var sessionId = Guid.NewGuid().ToString("N")[..12];
                    await InteractiveSessionMaterializer.MaterializeToDirectoryAsync(
                        sessionId: sessionId,
                        roomDirectoryPath: roomDirectoryPath,
                        adapter: primaryVendor,
                        model: null,
                        workingDirectory: workDir,
                        initialMessage: customPrompt).ConfigureAwait(true);
                }
            }
            else
            {
                await BuiltInWorkflowTemplates.MaterializeToDirectoryAsync(
                    templateId,
                    primaryVendor,
                    secondaryVendor,
                    roomDirectoryPath,
                    customPrompt,
                    secondaryCustomPrompt).ConfigureAwait(true);
            }

            MaterializedRoomDirectoryPath = roomDirectoryPath;
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Surfaces a failed Start attempt in-window instead of silently closing (the prior behavior --
    /// closing on any failure with no message at all -- was especially misleading once room
    /// name collisions started failing closed instead of silently overwriting the earlier room, see
    /// <c>RoomDirectoryAlreadyExistsException</c>). Leaves the window open so the user can pick a
    /// different name and retry without re-entering everything else.
    /// </summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
