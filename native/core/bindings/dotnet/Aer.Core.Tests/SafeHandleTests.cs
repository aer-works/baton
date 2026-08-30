namespace Aer.Core.Tests;

public class SafeHandleTests
{
    [Fact]
    public void AerTaskHandle_DisposesWithoutCrash()
    {
        string program = OperatingSystem.IsWindows() ? "cmd" : "echo";

        using AerTaskHandle handle = NativeMethods.aer_task_new(program, nint.Zero, 0);

        Assert.False(handle.IsInvalid);
        // 'using' calls ReleaseHandle → aer_task_free; ASAN/valgrind on Linux CI catches any leak or double-free
    }

    [Fact]
    public void AerCancelHandle_DisposesWithoutCrash()
    {
        string program = OperatingSystem.IsWindows() ? "cmd" : "echo";

        using AerTaskHandle task = NativeMethods.aer_task_new(program, nint.Zero, 0);
        Assert.False(task.IsInvalid);

        using AerCancelHandle cancel = NativeMethods.aer_task_make_cancel_handle(task);
        Assert.False(cancel.IsInvalid);
        // Both handles disposed via 'using' — proves aer_cancel_free and aer_task_free run without crash
    }
}
