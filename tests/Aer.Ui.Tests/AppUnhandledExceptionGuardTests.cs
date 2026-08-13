using Aer.Adapters;
using Aer.Ui.Core;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// Verifies the app-level unhandled exception guard (#1176).
/// Driven inside <see cref="TestAppBuilder"/>'s headless Avalonia session (<c>[AvaloniaFact]</c>),
/// which initializes <see cref="App"/> and <see cref="Dispatcher.UIThread"/>.
/// </summary>
public class AppUnhandledExceptionGuardTests
{
    [AvaloniaFact]
    public async Task Unhandled_exception_on_dispatcher_is_caught_logged_and_surfaced_without_crashing()
    {
        var tempAerHome = Path.Combine(Path.GetTempPath(), $"aer-home-test-{Guid.NewGuid():N}");
        var originalAerHome = Environment.GetEnvironmentVariable(AerPaths.HomeEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, tempAerHome);

            var window = new MainWindow();
            if (Avalonia.Application.Current is { } app && app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }

            var tcs = new TaskCompletionSource<bool>();
            var expectedMessage = $"Simulated unhandled exception {Guid.NewGuid():N}";

            // Post work to the dispatcher that throws an unhandled exception
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    throw new InvalidOperationException(expectedMessage);
                }
                finally
                {
                    // Signal completion on next tick after exception is processed
                    Dispatcher.UIThread.Post(() => tcs.TrySetResult(true));
                }
            });

            await tcs.Task;

            // Verify durable sink logged the exception under AER_HOME
            var logPath = AppUnhandledExceptionSink.LogPath;
            Assert.True(File.Exists(logPath), $"Expected log file to exist at {logPath}");

            var logContent = await File.ReadAllTextAsync(logPath);
            Assert.Contains(expectedMessage, logContent);

            // Verify error was surfaced through existing failure surface
            var expectedPrefix = PlainLanguage.ForUnexpectedAppError();
            var surfacedText = window.ViewModel.Chat.StatusText;
            if (string.IsNullOrEmpty(surfacedText))
            {
                surfacedText = window.ViewModel.RunStatusText;
            }

            Assert.Contains(expectedPrefix, surfacedText);
            Assert.Contains(expectedMessage, surfacedText);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AerPaths.HomeEnvironmentVariable, originalAerHome);
            if (Directory.Exists(tempAerHome))
            {
                try
                {
                    Directory.Delete(tempAerHome, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
