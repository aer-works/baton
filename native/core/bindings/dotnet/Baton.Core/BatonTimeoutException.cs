namespace Baton.Core;

/// <summary>
/// Thrown when an <see cref="BatonTask"/> run is killed because it exceeded its configured
/// wall-clock timeout (<see cref="BatonErrorCode.TimedOut"/>).
/// </summary>
public sealed class BatonTimeoutException : BatonException
{
    /// <summary>Creates a timeout exception with a default message.</summary>
    public BatonTimeoutException()
        : base(BatonErrorCode.TimedOut, "AER task was killed because it exceeded its configured timeout.")
    {
    }

    /// <summary>Creates a timeout exception with an explicit message.</summary>
    public BatonTimeoutException(string message)
        : base(BatonErrorCode.TimedOut, message)
    {
    }

    /// <summary>Creates a timeout exception with an explicit message and inner exception.</summary>
    public BatonTimeoutException(string message, Exception innerException)
        : base(BatonErrorCode.TimedOut, message, innerException)
    {
    }
}
