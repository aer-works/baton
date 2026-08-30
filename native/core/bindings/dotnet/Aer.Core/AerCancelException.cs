namespace Aer.Core;

/// <summary>
/// Thrown when an <see cref="AerTask"/> run is killed because it was cancelled — either via the
/// <see cref="CancellationToken"/> passed to <see cref="AerTask.RunAsync"/> or an explicit cancel
/// request (<see cref="AerErrorCode.Cancelled"/>).
/// </summary>
public sealed class AerCancelException : AerException
{
    /// <summary>Creates a cancellation exception with a default message.</summary>
    public AerCancelException()
        : base(AerErrorCode.Cancelled, "AER task was cancelled.")
    {
    }

    /// <summary>Creates a cancellation exception with an explicit message.</summary>
    public AerCancelException(string message)
        : base(AerErrorCode.Cancelled, message)
    {
    }

    /// <summary>Creates a cancellation exception with an explicit message and inner exception.</summary>
    public AerCancelException(string message, Exception innerException)
        : base(AerErrorCode.Cancelled, message, innerException)
    {
    }
}
