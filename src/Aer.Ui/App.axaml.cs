using Aer.Ui.Core;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Aer.Ui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // #823: the old Documents/AER Flow folder is real data on real installs. Migrate it to
            // Documents/Baton before the window that reads from that path opens; if both already
            // exist, don't guess which one wins — say so instead.
            // Either non-Migrated outcome carries a message and leaves the data where it was; the
            // window opens on the new path either way (#863).
            var migration = WorkspaceMigration.Run();
            if (migration.Message is { } migrationNotice)
            {
                new WorkspaceMigrationNoticeWindow(migrationNotice).Show();
            }

            // #1068: apply the remembered Settings → Appearance theme before the window is shown, so a
            // saved Light/Dark choice never flashes the OS default first. A tiny local-file read at
            // startup with nothing else pumping yet; a missing value resolves to "follow the OS".
            AppearanceTheme.Apply(LocalUiConfigurationStore.CreateDefault().LoadThemeAsync().GetAwaiter().GetResult());

            var window = new MainWindow();
            desktop.MainWindow = window;
            ErrorSurface = window.ViewModel;

            _ = RunStartupAsync(window, desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Where an unexpected error goes on screen (#1176). The window's own view model, held here
    /// rather than reached for through <see cref="IClassicDesktopStyleApplicationLifetime"/>: the
    /// guard must work wherever the app is hosted, and the headless session the UI tests run in has
    /// no desktop lifetime at all — resolving the surface through one meant the surfacing half of
    /// the guard could never be exercised by a test, which is how it shipped unverified the first
    /// time.
    /// </summary>
    internal MainWindowViewModel? ErrorSurface { get; set; }

    /// <summary>
    /// Issue #1176: the one app-level unhandled exception guard. What a person should see when it
    /// fires is the "Unexpected app error" row of <c>design/interaction-states.json</c>; this is
    /// only the wiring that makes it so. Why the hook must exist at all — what becomes of an
    /// exception thrown out of one of this app's nine <c>async void</c> handlers — is recorded
    /// once, on <c>AppUnhandledExceptionGuardTests</c>, where it is also proven.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppUnhandledExceptionSink.LogException(e.Exception);

        if (ErrorSurface is { } surface)
        {
            var message = $"{PlainLanguage.ForUnexpectedAppError()}: {e.Exception.Message}";
            if (surface.Chat is { } chat)
            {
                chat.StatusText = message;
            }
            else
            {
                surface.RunStatusText = message;
            }
        }
    }

    /// <summary>
    /// Sequences the window's startup so the two ways a room can be opened never race. First
    /// <see cref="MainWindow.InitializeAsync"/> populates Local UI Configuration's recents (UI spec
    /// §3.1, §4) and the switcher, so the window is useful on a bare launch too. Then exactly one of:
    /// <list type="bullet">
    /// <item>a launch argument (<c>aer-ui &lt;room-directory&gt;</c>) opens that directory directly through
    /// <see cref="MainWindow.OpenAsync"/> — which is what makes a directory opened this way get
    /// remembered in the recents list exactly like one opened by hand (#118/#119); or</item>
    /// <item>on a bare launch, <see cref="MainWindow.LandOnTopRoomAsync"/> lands on the top room
    /// (rooms-as-root, #1055).</item>
    /// </list>
    /// They are mutually exclusive and awaited in order: an explicit room argument is authoritative and
    /// the auto-landing does not also fire, so the two never compete to set the session's current room
    /// (#1055 second-reader). A missing/extra argument leaves the window on its rooms-as-root landing
    /// rather than failing to launch — a GUI app has no stderr/exit-code convention to fail into the way
    /// Aer.Cli does.
    /// </summary>
    private static async Task RunStartupAsync(MainWindow window, string[] args)
    {
        await window.InitializeAsync();

        if (args.Length == 1)
        {
            await window.OpenAsync(args[0]);
        }
        else
        {
            await window.LandOnTopRoomAsync();
        }
    }

    public void MenuShow_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.Activate();
        }
    }

    public void MenuExit_Click(object? sender, System.EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is MainWindow mainWindow)
        {
            mainWindow.ConfirmCloseAndExit();
        }
    }
}
