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

            _ = RunStartupAsync(window, desktop.Args ?? []);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Issue #1176: App-level unhandled exception guard. Prevents process termination, appends
    /// the exception durably to the AER-home sink, and surfaces it through existing non-modal failure text.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppUnhandledExceptionSink.LogException(e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is MainWindow mainWindow)
        {
            var message = $"{PlainLanguage.ForUnexpectedAppError()}: {e.Exception.Message}";
            if (mainWindow.ViewModel?.Chat is { } chat)
            {
                chat.StatusText = message;
            }
            else if (mainWindow.ViewModel != null)
            {
                mainWindow.ViewModel.RunStatusText = message;
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
