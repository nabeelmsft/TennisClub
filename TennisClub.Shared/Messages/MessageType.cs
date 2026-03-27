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
    GetBookings
}
