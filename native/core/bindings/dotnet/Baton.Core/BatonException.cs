namespace Baton.Core;

/// <summary>
/// Base exception for failures reported by the native <c>aer_core</c> library. Carries the
/// <see cref="BatonErrorCode"/> the native call returned so callers can branch on it without parsing
/// the message text.
/// </summary>
public class BatonException : Exception
{
    /// <summary>The native error code this exception represents.</summary>
    public BatonErrorCode ErrorCode { get; }

    /// <summary>Creates an exception with <see cref="BatonErrorCode.Panic"/> and a generic message.</summary>
    public BatonException()
        : this(BatonErrorCode.Panic, "AER operation failed.")
    {
    }

    /// <summary>Creates an exception with <see cref="BatonErrorCode.Panic"/> and the given message.</summary>
    public BatonException(string message)
        : this(BatonErrorCode.Panic, message)
    {
    }

    /// <summary>Creates an exception with <see cref="BatonErrorCode.Panic"/>, the given message, and inner exception.</summary>
    public BatonException(string message, Exception innerException)
        : this(BatonErrorCode.Panic, message, innerException)
    {
    }

    /// <summary>Creates an exception for the given error code with a default message.</summary>
    public BatonException(BatonErrorCode errorCode)
        : this(errorCode, $"AER operation failed with error code {errorCode}.")
    {
    }

    /// <summary>Creates an exception for the given error code with an explicit message.</summary>
    public BatonException(BatonErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Creates an exception for the given error code with an explicit message and inner exception.</summary>
    public BatonException(BatonErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
