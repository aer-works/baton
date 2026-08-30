namespace Aer.Core;

/// <summary>
/// Thrown when an <see cref="AerTask"/> run is killed because it exceeded its configured
/// wall-clock timeout (<see cref="AerErrorCode.TimedOut"/>).
/// </summary>
public sealed class AerTimeoutException : AerException
{
    /// <summary>Creates a timeout exception with a default message.</summary>
    public AerTimeoutException()
        : base(AerErrorCode.TimedOut, "AER task was killed because it exceeded its configured timeout.")
    {
    }

    /// <summary>Creates a timeout exception with an explicit message.</summary>
    public AerTimeoutException(string message)
        : base(AerErrorCode.TimedOut, message)
    {
    }

    /// <summary>Creates a timeout exception with an explicit message and inner exception.</summary>
    public AerTimeoutException(string message, Exception innerException)
        : base(AerErrorCode.TimedOut, message, innerException)
    {
    }
}
