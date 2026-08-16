using Aer.Ui.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Aer.Ui.Tests;

/// <summary>
/// Found while fixing #342 (PR #1285's second reader): the "Start a Room" picker's own vendor combo
/// has the identical raw-adapter-key-as-primary-text defect the chat composer's picker had —
/// <see cref="VendorComboItemTemplate"/> only ever applied the icon+brand-color treatment (#250),
/// never the display-name one. See #1286.
/// </summary>
public class TemplatePickerVendorComboTests
{
    [AvaloniaFact]
    public void ThePrimaryVendorCombo_ShowsDisplayNames_NotRawContractKeys()
    {
        var window = new TemplatePickerWindow
        {
            Width = 580,
            Height = 740,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.ApplyTemplate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var combo = window.PrimaryVendorCombo;
        var texts = combo.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Claude", texts);
        Assert.DoesNotContain("claude", texts);
        Assert.DoesNotContain("agy", texts);
    }
}
