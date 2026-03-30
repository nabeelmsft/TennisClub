using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace TennisClub.Service;

/// <summary>
/// Keeps a thread-safe registry of every connected WebSocket client.
/// Each entry pairs the socket with a SemaphoreSlim(1,1) send-lock so that
/// three concurrent writers — the response send in HandleClientAsync, the
/// heartbeat ping from HeartbeatMonitor, and BroadcastAsync — can never call
/// WebSocket.SendAsync on the same socket at the same time (which would throw).
/// </summary>
public class ConnectionManager
{
    private readonly ConcurrentDictionary<Guid, (WebSocket Socket, SemaphoreSlim SendLock)> _clients = new();

    /// <summary>
    /// Registers a new WebSocket and returns its unique ID together with the
    /// exclusive send-lock that every writer for this connection must acquire.
    /// </summary>
    public (Guid Id, SemaphoreSlim SendLock) Add(WebSocket socket)
    {
        var id = Guid.NewGuid();
        var sendLock = new SemaphoreSlim(1, 1);
        _clients[id] = (socket, sendLock);
        Console.WriteLine($"[ConnectionManager] Client registered. Total: {_clients.Count}");
        return (id, sendLock);
    }

    public void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var entry))
            entry.SendLock.Dispose();
        Console.WriteLine($"[ConnectionManager] Client removed. Total: {_clients.Count}");
    }

    /// <summary>
    /// Sends <paramref name="json"/> to every open client.
    /// Each send acquires the per-socket lock to avoid racing with the heartbeat
    /// pings or in-flight response writes on the same socket.
    /// </summary>
    public async Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var tasks = _clients.Values
            .Where(c => c.Socket.State == WebSocketState.Open)
            .Select(async c =>
            {
                await c.SendLock.WaitAsync();
                try
                {
                    await c.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally
                {
                    c.SendLock.Release();
                }
            });

        await Task.WhenAll(tasks);
        Console.WriteLine($"[ConnectionManager] Broadcast sent to {_clients.Count} client(s).");
    }
}
