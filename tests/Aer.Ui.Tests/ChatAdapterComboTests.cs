using Aer.Adapters;
using Aer.Ui.Core;
using Aer.Ui.Tests.TestSupport;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// #981: the new-chat adapter ComboBox rendered blank while the ViewModel held a valid value.
/// The mechanism and the shape of the fix are documented once, beside the binding they describe —
/// the ChatNewAdapterCombo comment in ChatView.axaml. These tests pin the write-back channel
/// shut; the visual half was verified live (#981's measurements are explicit that headless
/// property assertions alone stayed green while the screen was blank).
/// </summary>
public class ChatAdapterComboTests
{
    private static string NewConfigFilePath() =>
        Path.Combine(Path.GetTempPath(), $"aer-ui-combo-config-{Guid.NewGuid():N}", "recent-room-directories.json");

    private static readonly VendorCliStatus[] BothVendors =
    [
        new("claude", "claude", IsAvailable: true),
        new("agy", "agy", IsAvailable: true),
    ];

    [AvaloniaFact]
    public void Repopulating_adapters_keeps_the_held_selection_and_the_rendered_one()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var combo = window.FindViewControl<ComboBox>("ChatNewAdapterCombo")!;
        var chat = window.ViewModel.Chat;

        chat.PopulateAvailableAdapters(BothVendors);
        chat.NewChatAdapter = "agy";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("agy", combo.SelectedItem);

        // The #981 trigger: a second populate clears and refills ItemsSource. Under the old
        // two-way binding the control's cleared selection wrote null back into the ViewModel here.
        chat.PopulateAvailableAdapters(BothVendors);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("agy", chat.NewChatAdapter);
        Assert.Equal("agy", combo.SelectedItem);
    }

    [AvaloniaFact]
    public void A_pick_in_the_combo_still_reaches_the_view_model()
    {
        // Polarity for the one-way binding: narrowing the channel must not sever the upward half —
        // a genuine selection in the control still lands in NewChatAdapter (the SelectionChanged
        // handler), or the picker would render choices it silently ignores.
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()));
        var combo = window.FindViewControl<ComboBox>("ChatNewAdapterCombo")!;
        var chat = window.ViewModel.Chat;

        chat.PopulateAvailableAdapters(BothVendors);
        Dispatcher.UIThread.RunJobs();

        combo.SelectedItem = "agy";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("agy", chat.NewChatAdapter);
    }

    /// <summary>
    /// #342's second defect: the combo's items are adapter contract keys ("claude"/"agy"), not
    /// display text — the same internal-identifier-as-primary-text leak the vocabulary lint
    /// polices elsewhere, just not reachable by that lint since it only covers string literals.
    /// </summary>
    [AvaloniaFact]
    public void TheAdapterComboShowsDisplayNames_NotRawContractKeys()
    {
        var window = new MainWindow(new LocalUiConfigurationStore(NewConfigFilePath()))
        {
            Width = 900,
            Height = 700,
        };
        window.ViewModel.CurrentSection = ShellSection.Chat;
        var chat = window.ViewModel.Chat;
        chat.PopulateAvailableAdapters(BothVendors);
        chat.NewChatAdapter = "agy";
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var combo = window.FindViewControl<ComboBox>("ChatNewAdapterCombo")!;
        var texts = combo.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Gemini", texts);
        Assert.DoesNotContain("agy", texts);
        Assert.DoesNotContain("claude", texts);
    }
}
