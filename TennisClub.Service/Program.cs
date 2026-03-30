using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TennisClub.Service;
using TennisClub.Service.Handlers;
using TennisClub.Service.Services;
using TennisClub.Shared;
using TennisClub.Shared.Messages;

var memberService = new MemberService();
var bookingService = new BookingService();
var connections = new ConnectionManager();
var handler = new MessageHandler(memberService, bookingService, connections);

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();

Console.WriteLine("Tennis Club WebSocket server listening on ws://localhost:5000/");
Console.WriteLine("Press Ctrl+C to stop.");

while (true)
{
    var context = await listener.GetContextAsync();
    if (context.Request.IsWebSocketRequest)
        _ = Task.Run(() => HandleClientAsync(context, handler, connections));
    else
    {
        context.Response.StatusCode = 400;
        context.Response.Close();
    }
}

static async Task HandleClientAsync(HttpListenerContext context, MessageHandler handler, ConnectionManager connections)
{
    var wsContext = await context.AcceptWebSocketAsync(null);
    var ws = wsContext.WebSocket;
    Console.WriteLine($"Client connected from {context.Request.RemoteEndPoint}");

    // Register the socket and get back both its ID and its exclusive send-lock.
    // The lock is shared with HeartbeatMonitor and ConnectionManager.BroadcastAsync
    // so that only one writer ever calls ws.SendAsync at a time.
    var (connectionId, sendLock) = connections.Add(ws);

    // Start the heartbeat monitor for this connection on a background task.
    // The CancellationTokenSource lets us shut it down cleanly in the finally block.
    using var cts = new CancellationTokenSource();
    var heartbeat     = new HeartbeatMonitor(ws, sendLock, connectionId);
    var heartbeatTask = Task.Run(() => heartbeat.RunAsync(cts.Token));

    var buffer = new byte[8192];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            var json = Encoding.UTF8.GetString(ms.ToArray());
            var message = JsonSerializer.Deserialize<WebSocketMessage>(json, JsonConfig.Options);
            if (message is null) continue;

            // Pong is infrastructure — record the timestamp and skip MessageHandler.
            // This keeps heartbeat logic out of the application-level handler.
            if (message.Type == MessageType.Pong)
            {
                heartbeat.RecordPong();
                Console.WriteLine($"[Heartbeat] Pong ← {connectionId}");
                continue;
            }

            var response      = handler.Handle(message);
            var responseJson  = JsonSerializer.Serialize(response, JsonConfig.Options);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);

            // Acquire the per-socket send-lock before writing.
            // HeartbeatMonitor.SendPingAsync and BroadcastAsync both compete for
            // this socket; the lock guarantees only one writer is active at a time.
            await sendLock.WaitAsync();
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error handling client: {ex.Message}");
    }
    finally
    {
        // Cancel the heartbeat loop and wait for it to finish before removing
        // the connection from the registry (which also disposes the send-lock).
        cts.Cancel();
        await heartbeatTask;
        connections.Remove(connectionId);
    }

    Console.WriteLine($"Client disconnected from {context.Request.RemoteEndPoint}");
}
