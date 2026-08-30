namespace Aer.Core;

/// <summary>
/// Base exception for failures reported by the native <c>aer_core</c> library. Carries the
/// <see cref="AerErrorCode"/> the native call returned so callers can branch on it without parsing
/// the message text.
/// </summary>
public class AerException : Exception
{
    /// <summary>The native error code this exception represents.</summary>
    public AerErrorCode ErrorCode { get; }

    /// <summary>Creates an exception with <see cref="AerErrorCode.Panic"/> and a generic message.</summary>
    public AerException()
        : this(AerErrorCode.Panic, "AER operation failed.")
    {
    }

    /// <summary>Creates an exception with <see cref="AerErrorCode.Panic"/> and the given message.</summary>
    public AerException(string message)
        : this(AerErrorCode.Panic, message)
    {
    }

    /// <summary>Creates an exception with <see cref="AerErrorCode.Panic"/>, the given message, and inner exception.</summary>
    public AerException(string message, Exception innerException)
        : this(AerErrorCode.Panic, message, innerException)
    {
    }

    /// <summary>Creates an exception for the given error code with a default message.</summary>
    public AerException(AerErrorCode errorCode)
        : this(errorCode, $"AER operation failed with error code {errorCode}.")
    {
    }

    /// <summary>Creates an exception for the given error code with an explicit message.</summary>
    public AerException(AerErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Creates an exception for the given error code with an explicit message and inner exception.</summary>
    public AerException(AerErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
