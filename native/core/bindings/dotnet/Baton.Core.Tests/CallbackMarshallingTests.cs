using System.Text;

namespace Baton.Core.Tests;

public class CallbackMarshallingTests
{
    private static (string Program, string[] Args) EchoHello() =>
        OperatingSystem.IsWindows()
            ? ("cmd", ["/c", "echo", "hello"])
            : ("echo", ["hello"]);

    [Fact]
    public void Callback_ReceivesStartedAndExited()
    {
        (string prog, string[] args) = EchoHello();
        List<BatonEvent> events = [];

        using BatonTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        using CallbackBridge bridge = new((evt, _) => events.Add(evt));
        BatonErrorCode result = NativeMethods.aer_task_run(
            task, bridge.NativeCallback, nint.Zero);

        Assert.Equal(BatonErrorCode.Ok, result);
        Assert.Contains(events, e => e.Kind == BatonEventKind.Started);
        Assert.Contains(events, e => e.Kind == BatonEventKind.Exited);
        BatonEvent exited = events.Single(e => e.Kind == BatonEventKind.Exited);
        Assert.Equal(0, exited.Code);
        Assert.Equal((uint)BatonExitReason.Natural, exited.Reason);
    }

    [Fact]
    public void Callback_CopiesChunkBytesForStdoutEvent()
    {
        (string prog, string[] args) = EchoHello();
        List<byte[]> chunks = [];

        using BatonTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        _ = NativeMethods.aer_task_with_capture_output(task, true);

        using CallbackBridge bridge = new((evt, data) =>
        {
            if (evt.Kind == BatonEventKind.StdoutChunk && data != null)
            {
                chunks.Add(data);
            }
        });
        BatonErrorCode result = NativeMethods.aer_task_run(
            task, bridge.NativeCallback, nint.Zero);

        Assert.Equal(BatonErrorCode.Ok, result);
        Assert.NotEmpty(chunks);
        string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
        Assert.Contains("hello", output, StringComparison.OrdinalIgnoreCase);
    }
}
