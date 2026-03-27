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

    var connectionId = connections.Add(ws);
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

            var response = handler.Handle(message);
            var responseJson = JsonSerializer.Serialize(response, JsonConfig.Options);
            var responseBytes = Encoding.UTF8.GetBytes(responseJson);
            await ws.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error handling client: {ex.Message}");
    }
    finally
    {
        connections.Remove(connectionId);
    }

    Console.WriteLine($"Client disconnected from {context.Request.RemoteEndPoint}");
}
