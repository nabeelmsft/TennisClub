namespace TennisClub.Shared.Messages;

/// <summary>
/// Every request from client to server carries one of these types.
/// The server switches on it to route to the correct handler.
/// </summary>
public enum MessageType
{
    SignUp,
    GetMembers,
    GetAvailability,
    BookCourt,
    GetBookings,

    // Server-push: broadcast to ALL connected clients when any booking is confirmed.
    // No RequestId is matched on the client side — it arrives unsolicited.
    BookingBroadcast
}
