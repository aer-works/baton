using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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

    /// <summary>
    /// #1189, the other half of the app's async surface: <c>_ = SomethingAsync()</c>. Its exception
    /// never reaches the dispatcher — it sits on a Task nobody awaits — so the fact above cannot see
    /// it and this one drives the finalizer instead.
    /// </summary>
    /// <remarks>
    /// A plain <c>[Fact]</c>, NOT an <c>[AvaloniaFact]</c>, and that is the point of #1200: forcing
    /// a collection inside Avalonia's shared headless session destabilised it — green locally,
    /// intermittently red on Windows CI, and the failure surfaced as a cleanup error in an
    /// unrelated place. <see cref="UnobservedTaskGuard"/> needs no UI, so the fact takes none, and
    /// registers its own surface instead of borrowing the app's.
    /// </remarks>
    [Fact]
    public async Task An_exception_on_a_task_nobody_awaited_is_still_written_down()
    {
        var expectedMessage = $"Simulated unobserved fault {Guid.NewGuid():N}";
        // Concurrent, because the guard calls this from the finalizer thread while the poll below
        // reads it from the test's own.
        var surfaced = new ConcurrentQueue<Exception>();
        // One delegate instance, held: registration is additive, so unregistering needs the same
        // reference back. `surfaced.Enqueue` written twice would be two delegates, and would leak the first.
        Action<Exception> surface = surfaced.Enqueue;
        UnobservedTaskGuard.Register(surface);

        try
        {
            await DriveTheFinalizerAsync(expectedMessage, surfaced);
        }
        finally
        {
            UnobservedTaskGuard.Unregister(surface);
        }
    }

    /// <summary>
    /// #1201: the sink writes while something else holds the log open. Two Baton windows are two
    /// processes writing the same file, and the sink's own catch-all means a rejected write is
    /// indistinguishable from an exception that never happened — the silence it exists to end.
    /// </summary>
    /// <remarks>
    /// The holder here takes write access and shares read+write, which is what a second sink looks
    /// like from the outside; under the old <c>File.AppendAllText</c> (write access, sharing read
    /// only) the write is refused and the entry vanishes. One case stays uncovered and unfixable
    /// from this side: a holder that shares nothing — an editor with the log open — locks the write
    /// out however this end opens the file.
    /// </remarks>
    [Fact]
    public void An_exception_is_written_down_even_while_another_writer_holds_the_log()
    {
        var expectedMessage = $"Simulated concurrent-writer fault {Guid.NewGuid():N}";
        var logPath = AppUnhandledExceptionSink.LogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        using (new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            AppUnhandledExceptionSink.LogException(new InvalidOperationException(expectedMessage));
        }

        Assert.Contains(expectedMessage, ReadSharedText(logPath));
    }

    private static async Task DriveTheFinalizerAsync(string expectedMessage, ConcurrentQueue<Exception> surfaced)
    {
        DropAFaultedTask(expectedMessage);

        // The event fires when the faulted Task is finalized, which is a collection away rather
        // than a duration away — hence a bounded poll around forced collections rather than a
        // sleep. wait-ok: the ceiling only bounds a regression; the loop exits on the first pass.
        var logPath = AppUnhandledExceptionSink.LogPath;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (File.Exists(logPath) && ReadSharedText(logPath).Contains(expectedMessage))
            {
                // Both halves: written down durably, AND handed to whatever shows it to a person.
                // The app's own surface is a dispatcher post, which is why this fact supplies its
                // own — the claim here is that the guard reports, not where the report lands.
                Assert.Contains(surfaced, ex => ex.Message == expectedMessage);
                return;
            }

            await Task.Delay(100); // wait-ok: a poll interval between forced collections; the 60s deadline above is the ceiling.
        }

        Assert.Fail(
            $"The unobserved fault never reached the durable sink at {logPath}. Surfaced meanwhile: "
            + $"[{string.Join(" | ", surfaced.Select(exception => exception.Message))}] — a fault that reached "
            + "the surface but not the file is #1201's shape (a rejected write swallowed by the sink), "
            + "not a finalizer event that never fired.");
    }

    /// <summary>
    /// Reads the log without locking its writer out. <see cref="File.ReadAllText(string)"/> opens
    /// with <see cref="FileShare.Read"/>, which denies write access for as long as the read is open —
    /// so a poll built on it intermittently rejects the very write it is waiting for, and the sink's
    /// catch-all turns that into a missing entry rather than an error (measured while fixing #1189;
    /// the sink's own half of it is #1201). A poll that can suppress its subject is not an
    /// instrument, so this one shares.
    /// </summary>
    private static string ReadSharedText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Its own method so the Task is unreachable the moment it returns; a local in the test body
    // can stay rooted for the whole method and never be finalized.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropAFaultedTask(string message)
    {
        _ = FailAsync(message);

        static async Task FailAsync(string message)
        {
            await Task.Yield();
            throw new InvalidOperationException(message);
        }
    }
}
