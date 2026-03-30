namespace TennisClub.Shared.Messages;

/// <summary>
/// Extension methods for <see cref="MessageType"/>.
/// This is the single source of truth for which operations mutate server state.
/// </summary>
public static class MessageTypeExtensions
{
    /// <summary>
    /// The declared set of write operations.
    /// Any MessageType NOT in this set is treated as a read-only query.
    /// To add a new write operation, add it here — nothing else needs to change.
    /// </summary>
    private static readonly HashSet<MessageType> WriteOperations =
    [
        MessageType.SignUp,
        MessageType.BookCourt
    ];

    /// <summary>
    /// Returns true when sending this message type will mutate state on the server.
    /// Used by the client transport layer to decide whether to request confirmation.
    /// </summary>
    public static bool IsWriteOperation(this MessageType type) =>
        WriteOperations.Contains(type);

    /// <summary>
    /// Returns a human-readable display name shown in confirmation dialogs and logs.
    /// </summary>
    public static string GetDisplayName(this MessageType type) =>
        type switch
        {
            MessageType.SignUp            => "Register New Member",
            MessageType.GetMembers        => "Get Members",
            MessageType.GetAvailability   => "Check Court Availability",
            MessageType.BookCourt         => "Book a Court",
            MessageType.GetBookings       => "Get Bookings",
            MessageType.BookingBroadcast  => "Booking Broadcast",
            MessageType.Ping              => "Heartbeat Ping",
            MessageType.Pong              => "Heartbeat Pong",
            _                             => type.ToString()
        };
}
