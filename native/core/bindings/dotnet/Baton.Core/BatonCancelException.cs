namespace Baton.Core;

/// <summary>
/// Thrown when an <see cref="BatonTask"/> run is killed because it was cancelled — either via the
/// <see cref="CancellationToken"/> passed to <see cref="BatonTask.RunAsync"/> or an explicit cancel
/// request (<see cref="BatonErrorCode.Cancelled"/>).
/// </summary>
public sealed class BatonCancelException : BatonException
{
    /// <summary>Creates a cancellation exception with a default message.</summary>
    public BatonCancelException()
        : base(BatonErrorCode.Cancelled, "AER task was cancelled.")
    {
    }

    /// <summary>Creates a cancellation exception with an explicit message.</summary>
    public BatonCancelException(string message)
        : base(BatonErrorCode.Cancelled, message)
    {
    }

    /// <summary>Creates a cancellation exception with an explicit message and inner exception.</summary>
    public BatonCancelException(string message, Exception innerException)
        : base(BatonErrorCode.Cancelled, message, innerException)
    {
    }
}
