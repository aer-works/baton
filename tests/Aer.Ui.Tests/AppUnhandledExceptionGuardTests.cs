using Aer.Ui.Core;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Aer.Ui.Tests;

/// <summary>
/// The app-level unhandled exception guard (#1176), driven through the shape that motivated it:
/// an <c>async void</c> handler, of which nine live in <c>Aer.Ui</c>. Such a handler's exception
/// is observable on no <see cref="Task"/> — it is rethrown on the captured synchronization
/// context, which on Avalonia means the dispatcher, and with nothing hooked there it ends the
/// process.
/// </summary>
/// <remarks>
/// <para>
/// What this fact can and cannot show: it runs in the test host's own process, so "the process
/// survived" is observed by the test continuing to run at all — the honest instrument for the
/// real claim (a shipped app staying up) is driving the app, which this is not. What it does pin
/// exactly is that the exception reaches the guard, that the guard marks it handled rather than
/// letting the dispatcher rethrow, that the detail lands in the durable sink, and that the
/// wording appears on the surface a person is already looking at.
/// </para>
/// <para>
/// The sink writes under <c>AerPaths.Root</c>, which every test in this assembly already has
/// redirected to a throwaway root by <c>tests/Shared/AerHomeRedirect.cs</c>'s module initializer —
/// so this test deliberately does NOT set <c>AER_HOME</c> itself. Doing that would repoint the
/// process-global variable out from under every other test running beside it.
/// </para>
/// </remarks>
public class AppUnhandledExceptionGuardTests
{
    [AvaloniaFact]
    public async Task An_exception_escaping_an_async_void_handler_is_handled_logged_and_surfaced()
    {
        var window = new MainWindow();
        var app = Assert.IsType<App>(Avalonia.Application.Current);
        var previousSurface = app.ErrorSurface;
        app.ErrorSurface = window.ViewModel;

        var expectedMessage = $"Simulated unhandled exception {Guid.NewGuid():N}";
        var handled = new TaskCompletionSource<bool>();

        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Observed AFTER App's own handler, which is subscribed first: if the guard did not
            // set Handled, this sees false and the assertion below fails rather than the test
            // passing on the strength of the exception merely having been raised.
            handled.TrySetResult(e.Handled);
        }

        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            ThrowFromAnAsyncVoidHandler(expectedMessage);

            // A ceiling, not a pace: the dispatcher raises this on the next turn of its own loop,
            // so a real failure here is "never", not "slow" — the wait only keeps a regression from
            // hanging the suite.
            var wasHandled = await handled.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(wasHandled, "The guard did not mark the exception handled, so the dispatcher would rethrow it.");

            var logPath = AppUnhandledExceptionSink.LogPath;
            Assert.True(File.Exists(logPath), $"Expected the durable sink at {logPath}.");
            Assert.Contains(expectedMessage, await File.ReadAllTextAsync(logPath));

            var surfaced = string.IsNullOrEmpty(window.ViewModel.Chat.StatusText)
                ? window.ViewModel.RunStatusText
                : window.ViewModel.Chat.StatusText;
            Assert.Contains(PlainLanguage.ForUnexpectedAppError(), surfaced);
            Assert.Contains(expectedMessage, surfaced);
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
            app.ErrorSurface = previousSurface;
        }
    }

    // The production shape, deliberately: `async void`, awaiting before it throws, so the throw
    // happens on the continuation rather than synchronously inside the caller.
    private static async void ThrowFromAnAsyncVoidHandler(string message)
    {
        await Task.Yield();
        throw new InvalidOperationException(message);
    }
}
