using System.Text.Json;

namespace TennisClub.Shared.Messages;

/// <summary>
/// The envelope the SERVER sends back to the CLIENT.
/// The RequestId matches the original <see cref="WebSocketMessage.RequestId"/>
/// so the client can resume the correct awaiting Task.
/// </summary>
public class WebSocketResponse
{
    public string RequestId { get; set; } = string.Empty;
    public MessageType Type { get; set; }
    public bool Success { get; set; }

    // When deserialised on the client, System.Text.Json gives us a JsonElement
    // for 'object?' fields. Call GetData<T>() to convert to the needed type.
    public object? Data { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// Helper used on the CLIENT side to deserialise the Data payload into
    /// a concrete type (e.g. Member, List of Booking, etc.).
    /// </summary>
    public T? GetData<T>()
    {
        if (Data is JsonElement el)
            return JsonSerializer.Deserialize<T>(el.GetRawText(), JsonConfig.Options);
        return default;
    }
}
