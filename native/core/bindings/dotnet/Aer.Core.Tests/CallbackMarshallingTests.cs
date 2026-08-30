using System.Text;

namespace Aer.Core.Tests;

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
        List<AerEvent> events = [];

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        using CallbackBridge bridge = new((evt, _) => events.Add(evt));
        AerErrorCode result = NativeMethods.aer_task_run(
            task, bridge.NativeCallback, nint.Zero);

        Assert.Equal(AerErrorCode.Ok, result);
        Assert.Contains(events, e => e.Kind == AerEventKind.Started);
        Assert.Contains(events, e => e.Kind == AerEventKind.Exited);
        AerEvent exited = events.Single(e => e.Kind == AerEventKind.Exited);
        Assert.Equal(0, exited.Code);
        Assert.Equal((uint)AerExitReason.Natural, exited.Reason);
    }

    [Fact]
    public void Callback_CopiesChunkBytesForStdoutEvent()
    {
        (string prog, string[] args) = EchoHello();
        List<byte[]> chunks = [];

        using AerTaskHandle task = NativeMethods.CreateTask(prog, args);
        Assert.False(task.IsInvalid);

        _ = NativeMethods.aer_task_with_capture_output(task, true);

        using CallbackBridge bridge = new((evt, data) =>
        {
            if (evt.Kind == AerEventKind.StdoutChunk && data != null)
            {
                chunks.Add(data);
            }
        });
        AerErrorCode result = NativeMethods.aer_task_run(
            task, bridge.NativeCallback, nint.Zero);

        Assert.Equal(AerErrorCode.Ok, result);
        Assert.NotEmpty(chunks);
        string output = Encoding.UTF8.GetString([.. chunks.SelectMany(b => b)]);
        Assert.Contains("hello", output, StringComparison.OrdinalIgnoreCase);
    }
}
