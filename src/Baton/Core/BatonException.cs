namespace Baton.Core;

/// <summary>
/// Base exception for failures reported by <see cref="BatonTask"/>. Carries the <see cref="BatonErrorCode"/>
/// so callers can branch on it without parsing the message text.
/// </summary>
public class BatonException : Exception
{
    /// <summary>The error code this exception represents.</summary>
    public BatonErrorCode ErrorCode { get; }

    /// <summary>Creates an exception for the given error code with a default message.</summary>
    public BatonException(BatonErrorCode errorCode)
        : this(errorCode, $"Baton operation failed with error code {errorCode}.")
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
