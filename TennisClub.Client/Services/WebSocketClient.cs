using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TennisClub.Shared;
using TennisClub.Shared.Messages;

namespace TennisClub.Client.Services;

public class WebSocketClient : IAsyncDisposable
{
    // Non-readonly so ReconnectAsync can replace it with a fresh instance.
    private ClientWebSocket _ws = new();

    // Serialises all writes to _ws (SendAsync, SendPongAsync) so they never
    // overlap.  WebSocket.SendAsync throws if called concurrently on one instance.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketResponse>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    // Stored so ReconnectAsync can open a fresh connection to the same endpoint.
    private string? _url;

    private const int MaxReconnectAttempts = 5;

    public async Task ConnectAsync(string url)
    {
        _url = url;
        await _ws.ConnectAsync(new Uri(url), CancellationToken.None);
        _ = Task.Run(ReceiveLoopAsync);
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the server sends a message that has no matching pending RequestId.
    /// This is a server-initiated push — the client never asked for it.
    /// Fires on a thread-pool thread; use Dispatcher.Invoke to update WPF controls.
    /// </summary>
    public event Action<WebSocketResponse>? PushReceived;

    /// <summary>
    /// Raised at the start of each auto-reconnection attempt.
    /// The int argument is the current attempt number (1 – MaxReconnectAttempts).
    /// Fires on a thread-pool thread.
    /// </summary>
    public event Action<int>? Reconnecting;

    /// <summary>
    /// Raised when a reconnection attempt succeeds and a fresh receive loop is running.
    /// Fires on a thread-pool thread.
    /// </summary>
    public event Action? Reconnected;

    /// <summary>
    /// Optional confirmation gate for write operations.
    /// Return true → the message is sent. Return false → OperationCanceledException.
    /// If left null, all write operations proceed without confirmation.
    /// </summary>
    public Func<MessageType, Task<bool>>? ConfirmWriteAsync { get; set; }

    // ── Send ─────────────────────────────────────────────────────────────────

    public async Task<WebSocketResponse> SendAsync(WebSocketMessage message)
    {
        if (message.Type.IsWriteOperation() && ConfirmWriteAsync is not null)
        {
            var confirmed = await ConfirmWriteAsync(message.Type);
            if (!confirmed)
                throw new OperationCanceledException($"{message.Type.GetDisplayName()} was cancelled by the user.");
        }

        var tcs = new TaskCompletionSource<WebSocketResponse>();
        _pending[message.RequestId] = tcs;

        var json    = JsonSerializer.Serialize(message, JsonConfig.Options);
        var bytes   = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        // Acquire the send-lock so this call never overlaps with SendPongAsync,
        // which can fire on the ReceiveLoopAsync thread at any moment.
        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await _ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch
        {
            _pending.TryRemove(message.RequestId, out _);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ── Receive loop ─────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                var json     = Encoding.UTF8.GetString(ms.ToArray());
                var response = JsonSerializer.Deserialize<WebSocketResponse>(json, JsonConfig.Options);
                if (response is null) continue;

                // Ping is infrastructure — reply immediately and skip _pending routing.
                if (response.Type == MessageType.Ping)
                {
                    _ = Task.Run(SendPongAsync);
                    continue;
                }

                if (_pending.TryRemove(response.RequestId, out var tcs))
                    tcs.SetResult(response);        // matched: a reply to our request
                else
                    PushReceived?.Invoke(response); // unmatched: server-initiated push
            }
        }
        catch (OperationCanceledException) { return; } // normal shutdown via DisposeAsync
        catch (Exception ex)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(ex);
        }

        // If we exit the loop without being cancelled the connection was lost.
        // Kick off automatic reconnection.
        if (!_cts.IsCancellationRequested)
            await ReconnectAsync();
    }

    // ── Heartbeat response ───────────────────────────────────────────────────

    private async Task SendPongAsync()
    {
        var pong    = new WebSocketMessage { Type = MessageType.Pong };
        var json    = JsonSerializer.Serialize(pong, JsonConfig.Options);
        var bytes   = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── Reconnection ─────────────────────────────────────────────────────────

    /// <summary>
    /// Called automatically by ReceiveLoopAsync when the connection drops unexpectedly.
    /// Uses exponential back-off: 2 s, 4 s, 8 s, 16 s, 30 s (capped).
    /// On success a fresh ReceiveLoopAsync is started and Reconnected is raised.
    /// On failure all pending TCSs are faulted so their awaiting callers unblock.
    /// </summary>
    private async Task ReconnectAsync()
    {
        for (var attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            Reconnecting?.Invoke(attempt);

            var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
            Console.WriteLine($"[WebSocketClient] Reconnecting in {delay.TotalSeconds}s (attempt {attempt}/{MaxReconnectAttempts})…");

            try { await Task.Delay(delay, _cts.Token); }
            catch (OperationCanceledException) { return; }

            try
            {
                _ws.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_url!), _cts.Token);

                // Connection restored — restart the receive loop and notify the UI.
                _ = Task.Run(ReceiveLoopAsync);
                Reconnected?.Invoke();
                Console.WriteLine("[WebSocketClient] Reconnected successfully.");
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocketClient] Reconnect attempt {attempt} failed: {ex.Message}");
            }
        }

        // All attempts exhausted — fault every pending request so callers unblock.
        var error = new Exception($"Could not reconnect after {MaxReconnectAttempts} attempts.");
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(error);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        _ws.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
