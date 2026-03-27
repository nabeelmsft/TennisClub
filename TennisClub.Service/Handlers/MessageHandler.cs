using System.Text.Json;
using TennisClub.Service.Services;
using TennisClub.Shared;
using TennisClub.Shared.Messages;
using TennisClub.Shared.Messages.Payloads;
using TennisClub.Shared.Models;

namespace TennisClub.Service.Handlers;

public class MessageHandler
{
    private readonly MemberService _members;
    private readonly BookingService _bookings;
    private readonly ConnectionManager _connections;

    public MessageHandler(MemberService members, BookingService bookings, ConnectionManager connections)
    {
        _members = members;
        _bookings = bookings;
        _connections = connections;
    }

    public WebSocketResponse Handle(WebSocketMessage message) =>
        message.Type switch
        {
            MessageType.SignUp          => HandleSignUp(message),
            MessageType.GetMembers      => HandleGetMembers(message),
            MessageType.GetAvailability => HandleGetAvailability(message),
            MessageType.BookCourt       => HandleBookCourt(message),
            MessageType.GetBookings     => HandleGetBookings(message),
            _                           => Error(message, "Unknown message type.")
        };

    private WebSocketResponse HandleSignUp(WebSocketMessage msg)
    {
        var payload = Deserialize<SignUpPayload>(msg.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.Email))
            return Error(msg, "Name and email are required.");

        return Ok(msg, _members.Add(payload.Name, payload.Email));
    }

    private WebSocketResponse HandleGetMembers(WebSocketMessage msg) =>
        Ok(msg, _members.GetAll());

    private WebSocketResponse HandleGetAvailability(WebSocketMessage msg)
    {
        var payload = Deserialize<GetAvailabilityPayload>(msg.Payload);
        if (payload is null) return Error(msg, "Date is required.");
        return Ok(msg, _bookings.GetAvailability(payload.Date));
    }

    private WebSocketResponse HandleBookCourt(WebSocketMessage msg)
    {
        var payload = Deserialize<BookCourtPayload>(msg.Payload);
        if (payload is null) return Error(msg, "Invalid booking payload.");

        var m1 = _members.GetById(payload.Member1Id);
        var m2 = _members.GetById(payload.Member2Id);
        if (m1 is null || m2 is null) return Error(msg, "One or both members not found.");

        var (success, error, booking) = _bookings.Book(
            payload.CourtNumber, m1.Id, m1.Name, m2.Id, m2.Name, payload.Date, payload.StartHour);

        if (success)
        {
            // Fire-and-forget: push the new booking to every connected client.
            // The requesting client gets the normal Ok() response AND the broadcast.
            // Other clients receive only the broadcast — with no matching RequestId,
            // their WebSocketClient routes it to the PushReceived event instead.
            _ = BroadcastBookingAsync(booking!);
            return Ok(msg, booking!);
        }

        return Error(msg, error!);
    }

    private async Task BroadcastBookingAsync(Booking booking)
    {
        var push = new WebSocketResponse
        {
            RequestId = string.Empty,   // No pending request to match — clients route by absence
            Type = MessageType.BookingBroadcast,
            Success = true,
            Data = booking
        };
        var json = JsonSerializer.Serialize(push, JsonConfig.Options);
        await _connections.BroadcastAsync(json);
    }

    private WebSocketResponse HandleGetBookings(WebSocketMessage msg) =>
        Ok(msg, _bookings.GetAll());

    private static T? Deserialize<T>(JsonElement? element)
    {
        if (element is null) return default;
        return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), JsonConfig.Options);
    }

    private static WebSocketResponse Ok(WebSocketMessage msg, object data) => new()
    {
        RequestId = msg.RequestId,
        Type = msg.Type,
        Success = true,
        Data = data
    };

    private static WebSocketResponse Error(WebSocketMessage msg, string error) => new()
    {
        RequestId = msg.RequestId,
        Type = msg.Type,
        Success = false,
        Error = error
    };
}
