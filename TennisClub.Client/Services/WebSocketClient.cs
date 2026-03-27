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
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketResponse>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    public async Task ConnectAsync(string url)
    {
        await _ws.ConnectAsync(new Uri(url), CancellationToken.None);
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task<WebSocketResponse> SendAsync(WebSocketMessage message)
    {
        var tcs = new TaskCompletionSource<WebSocketResponse>();
        _pending[message.RequestId] = tcs;

        var json = JsonSerializer.Serialize(message, JsonConfig.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (_ws.State == WebSocketState.Open)
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

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var response = JsonSerializer.Deserialize<WebSocketResponse>(json, JsonConfig.Options);
                if (response is not null && _pending.TryRemove(response.RequestId, out var tcs))
                    tcs.SetResult(response);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        _ws.Dispose();
        _cts.Dispose();
    }
}
