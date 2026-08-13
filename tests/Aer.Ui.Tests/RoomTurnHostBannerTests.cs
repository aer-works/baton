using System.Threading.Tasks;
using Aer.Adapters;
using Aer.Ui.Core;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Aer.Ui.Tests;

public class RoomTurnHostBannerTests
{
    private static readonly IReadOnlyDictionary<string, IWorkerAdapter> Adapters =
        new Dictionary<string, IWorkerAdapter>();

    private static MainWindow NewWindow() => new(
        new LocalUiConfigurationStore(Path.Combine(Path.GetTempPath(), $"aer-ui-turnhost-config-{Guid.NewGuid():N}", "recent-room-directories.json")),
        Adapters);

    private static RoomTurnHostStatus CreateStatus(
        int count = 3,
        int cap = 10,
        bool isDormant = false,
        int failures = 3,
        string source = "defaults",
        string? loadError = null,
        string? dormancyEscalationDetail = null)
    {
        return new RoomTurnHostStatus(
            RoomDirectoryPath: "/test/room",
            Throttles: new RoomTurnHostThrottleValues(60, cap, 3),
            ThrottlesSource: source,
            LoadError: loadError,
            MachineTurnsInTrailingHour: $"{count}/{cap}",
            TurnsInTrailingHourCount: count,
            MachineTurnsPerHourCap: cap,
            ConsecutiveFailures: failures,
            InFlight: false,
            IsDormant: isDormant,
            DormancyEscalationDetail: dormancyEscalationDetail,
            LastDecisionReason: isDormant ? "Dormant" : null);
    }

    [Fact]
    public void VM_MeterText_RendersTurnsInTrailingHourAndCap()
    {
        // Red arm note: If MeterText did not include "3/10" from TurnsInTrailingHourCount/MachineTurnsPerHourCap, this assertion fails.
        var status = CreateStatus(count: 3, cap: 10);
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.Contains("3/10", banner.MeterText);
        Assert.Null(banner.LoadErrorText);
    }

    [Fact]
    public void VM_LoadError_PopulatesLoadErrorText()
    {
        // Red arm note: If LoadError is non-null on status but LoadErrorText remains null on banner VM, this assertion fails.
        var status = CreateStatus(loadError: "Malformed turn-throttles.json");
        var banner = new RoomTurnHostBannerViewModel(status);

        Assert.NotNull(banner.LoadErrorText);
        Assert.Equal("Malformed turn-throttles.json", banner.LoadErrorText);
    }

    [Fact]
    public void VM_NullStatus_HasRoomTurnHostBannerReturnsFalse()
    {
        // Red arm note: If HasRoomTurnHostBanner returns true when RoomTurnHostBanner is set to null (absence polarity), this assertion fails.
        var vm = new MainWindowViewModel();
        vm.RoomTurnHostBanner = null;

        Assert.False(vm.HasRoomTurnHostBanner);
    }

    [AvaloniaFact]
    public void View_LoadError_TextBlockVisibility_FollowsLoadErrorText()
    {
        // Red arm note (second-reader finding): if the LoadErrorText IsVisible binding in
        // RoomView.axaml is broken (wrong path/converter), the error TextBlock either never
        // shows for a malformed turn-throttles.json or always shows an empty line — one of the
        // two polarity assertions below fails.
        var window = NewWindow();
        window.Show();

        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(
            CreateStatus(loadError: "Malformed turn-throttles.json"));
        var errorBlock = window.FindViewControl<Avalonia.Controls.TextBlock>("TurnHostMeterLoadError")!;
        Assert.True(errorBlock.IsVisible);
        Assert.Equal("Malformed turn-throttles.json", errorBlock.Text);

        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(CreateStatus(loadError: null));
        Assert.False(errorBlock.IsVisible);
    }

    [AvaloniaFact]
    public void View_Status_ShowsMeter()
    {
        // Red arm note: If the banner hides MeterText when ViewModel carries a turn-host status, this assertion fails.
        var window = NewWindow();
        var status = CreateStatus(count: 3, cap: 10);
        window.ViewModel.RoomTurnHostBanner = new RoomTurnHostBannerViewModel(status);

        Assert.True(window.ViewModel.HasRoomTurnHostBanner);
        Assert.NotNull(window.ViewModel.RoomTurnHostBanner);
        Assert.Contains("3/10", window.ViewModel.RoomTurnHostBanner.MeterText);
    }
}
