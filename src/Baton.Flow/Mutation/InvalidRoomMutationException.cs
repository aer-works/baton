namespace Baton.Flow.Mutation;

/// <summary>
/// Raised when a requested room mutation is invalid against the current <see cref="Projection.RoomState"/>.
/// </summary>
public sealed class InvalidRoomMutationException : BatonFlowException
{
    public InvalidRoomMutationException(string message)
        : base(message)
    {
    }

    public InvalidRoomMutationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
