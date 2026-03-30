using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TennisClub.Shared;
using TennisClub.Shared.Messages;

namespace TennisClub.Service;

/// <summary>
/// Runs a per-connection heartbeat loop on the server side.
///
/// Every <see cref="Interval"/> seconds the monitor acquires the shared send-lock
/// and sends a Ping message to the client.  The client must reply with a Pong within
/// <see cref="Timeout"/> seconds.  If no Pong arrives the socket is closed — the
/// underlying TCP connection has been silently dropped (e.g. NAT timeout, Wi-Fi
/// roam, laptop sleep) and we reclaim the server slot instead of leaving a zombie.
///
/// Why application-level Ping/Pong instead of RFC-6455 control frames?
/// The .NET ClientWebSocket does not expose an API to send or handle WebSocket
/// control frames (ping/pong at the protocol level).  Using a regular JSON message
/// gives identical reliability while staying inside the existing message pipeline.
/// </summary>
public class HeartbeatMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout  = TimeSpan.FromSeconds(25);

    private readonly WebSocket     _ws;
    private readonly SemaphoreSlim _sendLock;
    private readonly Guid          _connectionId;

    // volatile so RecordPong() (called from the receive loop) is visible to RunAsync
    // without a full lock.  DateTime is not volatile-compatible on all runtimes, so
    // we store the UTC ticks as a long and use Interlocked for atomic read/write.
    private long _lastPongTicks = DateTime.UtcNow.Ticks;

    public HeartbeatMonitor(WebSocket ws, SemaphoreSlim sendLock, Guid connectionId)
    {
        _ws           = ws;
        _sendLock     = sendLock;
        _connectionId = connectionId;
    }

    /// <summary>
    /// Updates the "last seen" timestamp when a Pong arrives.
    /// Called by HandleClientAsync in Program.cs before routing to MessageHandler.
    /// </summary>
    public void RecordPong() => Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);

    /// <summary>
    /// Runs the heartbeat loop until the CancellationToken is cancelled or the
    /// connection is declared dead.  Designed to run on a Task.Run background thread.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                await Task.Delay(Interval, ct);

                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPongTicks) > Timeout.Ticks)
                {
                    Console.WriteLine($"[Heartbeat] Connection {_connectionId} timed out — closing.");
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Heartbeat timeout", CancellationToken.None);
                    break;
                }

                await SendPingAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown — HandleClientAsync cancelled us */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[Heartbeat] Error on {_connectionId}: {ex.Message}");
        }
    }

    private async Task SendPingAsync(CancellationToken ct)
    {
        var ping  = new WebSocketMessage { Type = MessageType.Ping };
        var json  = JsonSerializer.Serialize(ping, JsonConfig.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Must acquire the per-socket lock before writing.
        // HandleClientAsync (response sends) and ConnectionManager.BroadcastAsync
        // both compete for the same socket; the lock serialises all three writers.
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        Console.WriteLine($"[Heartbeat] Ping → {_connectionId}");
    }
}
