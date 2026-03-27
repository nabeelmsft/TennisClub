using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace TennisClub.Service;

/// <summary>
/// Keeps a thread-safe registry of every connected WebSocket client.
/// Any part of the server can call BroadcastAsync to push a JSON message
/// to all clients simultaneously — no request needed from the clients.
/// This is the key feature that separates WebSockets from HTTP.
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    public Guid Add(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        Console.WriteLine($"[ConnectionManager] Client registered. Total: {_sockets.Count}");
        return id;
    }

    public void Remove(Guid id)
    {
        _sockets.TryRemove(id, out _);
        Console.WriteLine($"[ConnectionManager] Client removed. Total: {_sockets.Count}");
    }

    public async Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        // Send to every open socket in parallel.
        // Sockets that are closed or faulted are skipped silently.
        var tasks = _sockets.Values
            .Where(ws => ws.State == WebSocketState.Open)
            .Select(ws => ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None));

        await Task.WhenAll(tasks);
        Console.WriteLine($"[ConnectionManager] Broadcast sent to {_sockets.Count} client(s).");
    }
}
