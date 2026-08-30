using System.Runtime.InteropServices;

namespace Aer.Core.Tests;

public class NativeAbiTests
{
    [Fact]
    public void NativeLibrary_IsReachable()
    {
        // If the native library cannot be found this throws DllNotFoundException,
        // which fails the test with a clear message. Either return value (null or
        // a message pointer) proves the ABI loaded successfully.
        nint ptr = NativeMethods.aer_last_error_message();

        // No error has occurred yet, so NULL is the expected return.
        // A non-null pointer would also be valid — just marshal it for the assertion.
        if (ptr == nint.Zero)
        {
            Assert.True(true);
        }
        else
        {
            string? msg = Marshal.PtrToStringUTF8(ptr);
            Assert.NotNull(msg);
        }
    }
}
