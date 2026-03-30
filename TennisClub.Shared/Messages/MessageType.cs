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
    BookingBroadcast,

    // Heartbeat pair — infrastructure only, never routed to MessageHandler.
    // Server → Client: sent every 10 s to detect silently dropped connections.
    Ping,
    // Client → Server: reply to a Ping; server records the timestamp.
    Pong
}
