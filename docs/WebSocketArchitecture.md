# Tennis Club — WebSocket Architecture & Key Concepts

This document explains how the application works under the hood, with a focus on the
WebSocket patterns used. It is written for developers who are new to WebSockets.

---

## Table of Contents

1. [Why WebSockets?](#1-why-websockets)
2. [Application Architecture](#2-application-architecture)
3. [The Message Envelope Pattern](#3-the-message-envelope-pattern)
4. [The Core Problem: No Built-in Request/Response](#4-the-core-problem-no-built-in-requestresponse)
5. [The Solution: TaskCompletionSource + Pending Dictionary](#5-the-solution-taskcompletionsource--pending-dictionary)
6. [SendAsync — Line by Line](#6-sendasync--line-by-line)
7. [ReceiveLoopAsync — The Other Half](#7-receiveloopasync--the-other-half)
8. [Server-Push Broadcasting](#8-server-push-broadcasting)
9. [Full Message Flow Diagrams](#9-full-message-flow-diagrams)
10. [Threading Model](#10-threading-model)

---

## 1. Why WebSockets?

HTTP is a **pull** protocol — the client always initiates, the server always replies, and
the connection is closed after each exchange.

```
HTTP:
  Client ──[request]──► Server
  Client ◄──[response]─ Server
  (connection closed)
```

WebSocket is a **full-duplex persistent** channel. After a one-time HTTP "upgrade"
handshake, both sides can send messages to each other at any time, in any order,
over the same open connection.

```
WebSocket:
  Client ──[connect / HTTP upgrade]──► Server
  ─────────── connection stays open ───────────
  Client ──[message]──────────────────► Server
  Client ◄──[reply]───────────────────  Server
  Client ◄──[server push, unprompted]─  Server   ← HTTP cannot do this
  Client ──[message]──────────────────► Server
  ...
```

The Tennis Club application uses this to broadcast a booking notification to
**every connected client** the moment anyone makes a reservation — without any of
those clients having to poll or ask.

---

## 2. Application Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                        TennisClub.Shared                         │
│  Models: Member, Booking, CourtAvailability                      │
│  Messages: WebSocketMessage, WebSocketResponse, MessageType      │
│  Payloads: SignUpPayload, BookCourtPayload, GetAvailabilityPayload│
└───────────────────────────┬──────────────────────────────────────┘
                            │ referenced by both
          ┌─────────────────┴──────────────────┐
          │                                    │
┌─────────▼────────────┐            ┌──────────▼───────────────┐
│   TennisClub.Service │            │   TennisClub.Client       │
│   (Console App)      │            │   (WPF App)               │
│                      │            │                           │
│  Program.cs          │  WebSocket │  WebSocketClient.cs       │
│  ┌────────────────┐  │◄──────────►│  ┌─────────────────────┐ │
│  │HttpListener    │  │            │  │ClientWebSocket      │ │
│  │(ws://localhost │  │            │  │SendAsync()          │ │
│  │    :5000/)     │  │            │  │ReceiveLoopAsync()   │ │
│  └───────┬────────┘  │            │  │PushReceived event   │ │
│          │           │            │  └─────────────────────┘ │
│  ┌───────▼────────┐  │            │                           │
│  │ConnectionManager│ │            │  MainWindow.xaml.cs       │
│  │(all sockets)   │  │            │  ┌─────────────────────┐ │
│  └───────┬────────┘  │            │  │Members tab          │ │
│          │           │            │  │Availability tab     │ │
│  ┌───────▼────────┐  │            │  │Book a Court tab     │ │
│  │MessageHandler  │  │            │  │Bookings tab         │ │
│  │(routes by Type)│  │            │  │Live Feed tab        │ │
│  └───────┬────────┘  │            │  └─────────────────────┘ │
│          │           │            └──────────────────────────┘
│  ┌───────▼────────┐  │
│  │MemberService   │  │
│  │BookingService  │  │
│  │(in-memory data)│  │
│  └────────────────┘  │
└──────────────────────┘
```

---

## 3. The Message Envelope Pattern

Every message in both directions is wrapped in an **envelope** — a container that
carries routing metadata alongside the actual data.

### Client → Server: `WebSocketMessage`

```csharp
public class WebSocketMessage
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString(); // correlation ID
    public MessageType Type { get; set; }                              // what to do
    public JsonElement? Payload { get; set; }                          // the data
}
```

### Server → Client: `WebSocketResponse`

```csharp
public class WebSocketResponse
{
    public string RequestId { get; set; }  // echoed back from the request
    public MessageType Type { get; set; }
    public bool Success { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}
```

The `RequestId` is the key field. It is a GUID created fresh for every outgoing
message. The server **echoes it back unchanged** in its reply. This lets the client
match each response to the exact call that triggered it — something the WebSocket
protocol itself does not provide.

For **server-push** messages (broadcasts), the server sets `RequestId = string.Empty`
because there is no client request to correlate with.

---

## 4. The Core Problem: No Built-in Request/Response

This is the central challenge of building on WebSockets.

With HTTP you naturally get one response per request. With a WebSocket you have
**one shared pipe** that carries everything:

```
Time ──►

Client sends:  [GetMembers, id=AAA]──────────[BookCourt, id=BBB]────────────────────
                                                                                    
Server sends:  ──────────────────[broadcast push, id=""]──[BBB reply]──[AAA reply]──
```

Notice:
- Two requests were sent before any reply arrived
- A broadcast push arrived before either reply
- Replies arrived **out of order** (BBB before AAA)

Without the `RequestId` + `_pending` pattern, the client would have no way to
match the `BBB reply` to the `BookCourt` call or the `AAA reply` to the
`GetMembers` call.

---

## 5. The Solution: TaskCompletionSource + Pending Dictionary

```csharp
private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketResponse>> _pending = new();
```

This dictionary is the **waiting room**. Its key is a `RequestId` (GUID string).
Its value is a `TaskCompletionSource<WebSocketResponse>` — a manually-controlled
promise.

### What is a TaskCompletionSource?

Normally a `Task` completes when an `async` method returns. A
`TaskCompletionSource<T>` gives you a `Task<T>` that only completes when **you**
call `SetResult(value)` on it — from anywhere, at any time, on any thread.

Think of it as a **promise with a doorbell**:

```
tcs.Task      →  the promise  (the caller waits on this)
tcs.SetResult →  the doorbell (rings the promise, waking the caller up)
```

The `_pending` dictionary maps each in-flight `RequestId` to its doorbell.
When a reply arrives on the receive loop, it looks up the `RequestId`, finds the
doorbell, and rings it — instantly waking up whichever caller was suspended waiting
for that specific reply.

---

## 6. SendAsync — Line by Line

```csharp
public async Task<WebSocketResponse> SendAsync(WebSocketMessage message)
{
    // 1. Create a promise (doorbell) for this specific request
    var tcs = new TaskCompletionSource<WebSocketResponse>();

    // 2. Register it in the waiting room under this request's unique ID
    _pending[message.RequestId] = tcs;

    // 3. Serialise and send the message over the WebSocket pipe
    var json = JsonSerializer.Serialize(message, JsonConfig.Options);
    var bytes = Encoding.UTF8.GetBytes(json);
    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

    // 4. Suspend this caller until the doorbell rings (or 10 s timeout)
    return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
```

### Step-by-step

| Step | What happens |
|------|-------------|
| **1** | A fresh `TaskCompletionSource` is created — its `.Task` is unsignalled (the doorbell hasn't rung yet) |
| **2** | The TCS is stored in `_pending` keyed by `RequestId`. The receive loop will use this key to find it later |
| **3** | The message is serialised to JSON and written to the WebSocket pipe. This is the actual network send |
| **4** | The calling code (e.g., a button click handler) suspends here. Its thread is freed — no blocking occurs |

The caller stays suspended at step 4 until `ReceiveLoopAsync` rings the doorbell.

---

## 7. ReceiveLoopAsync — The Other Half

```csharp
private async Task ReceiveLoopAsync()
{
    var buffer = new byte[8192];
    while (_ws.State == WebSocketState.Open)
    {
        // Accumulate frames until a complete message arrives
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage); // WebSocket can split one logical message across many frames

        if (result.MessageType == WebSocketMessageType.Close) break;

        var json = Encoding.UTF8.GetString(ms.ToArray());
        var response = JsonSerializer.Deserialize<WebSocketResponse>(json, JsonConfig.Options);
        if (response is null) continue;

        if (_pending.TryRemove(response.RequestId, out var tcs))
            tcs.SetResult(response);        // ← matched: a reply to our request
        else
            PushReceived?.Invoke(response); // ← unmatched: server-initiated push
    }
}
```

### The `do...while (!result.EndOfMessage)` loop — WebSocket Framing

WebSocket allows one logical message to be split across multiple **frames** on the wire
(e.g., a large JSON payload may arrive in two chunks). The loop keeps reading frames
and appending them to a `MemoryStream` until `EndOfMessage` is `true`, at which point
the `MemoryStream` holds the complete logical message ready for deserialisation.

### The routing fork

```csharp
if (_pending.TryRemove(response.RequestId, out var tcs))
    tcs.SetResult(response);        // ← matched: rings the doorbell for a waiting caller
else
    PushReceived?.Invoke(response); // ← unmatched: nobody was waiting → it's a server push
```

| `RequestId` found in `_pending`? | Meaning | Action |
|---|---|---|
| ✅ Yes | Server replied to a request we made | Ring the doorbell → resume the suspended `SendAsync` caller |
| ❌ No | Server sent this without being asked | Fire `PushReceived` event → Live Feed and Bookings tab update |

---

## 8. Server-Push Broadcasting

This is the capability that makes WebSockets worth choosing over HTTP polling.

### ConnectionManager (Service)

```csharp
public class ConnectionManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    public Guid Add(WebSocket socket) { ... }    // called when a client connects
    public void Remove(Guid id) { ... }          // called when a client disconnects

    public async Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var tasks = _sockets.Values
            .Where(ws => ws.State == WebSocketState.Open)
            .Select(ws => ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None));

        await Task.WhenAll(tasks); // sends to all clients in parallel
    }
}
```

### Triggered by a booking (MessageHandler)

```csharp
if (success)
{
    _ = BroadcastBookingAsync(booking!); // fire-and-forget: don't make the booker wait
    return Ok(msg, booking!);            // reply to the client who made the request
}

private async Task BroadcastBookingAsync(Booking booking)
{
    var push = new WebSocketResponse
    {
        RequestId = string.Empty,          // no request to correlate with
        Type = MessageType.BookingBroadcast,
        Success = true,
        Data = booking
    };
    var json = JsonSerializer.Serialize(push, JsonConfig.Options);
    await _connections.BroadcastAsync(json);
}
```

### Received by the client (MainWindow.xaml.cs)

```csharp
private void OnPushReceived(WebSocketResponse response)
{
    Dispatcher.Invoke(() =>  // push arrives on a background thread; must marshal to UI thread
    {
        if (response.Type == MessageType.BookingBroadcast)
        {
            var booking = response.GetData<Booking>();
            if (booking is null) return;

            var line = $"[{DateTime.Now:HH:mm:ss}]  ▶  Court {booking.CourtNumber} ...";
            _liveFeed.Insert(0, line);

            if (!_bookings.Any(b => b.Id == booking.Id))
                _bookings.Add(booking);
        }
    });
}
```

---

## 9. Full Message Flow Diagrams

### Normal request/response (e.g., Sign Up)

```
MainWindow          WebSocketClient         Network          Service
    │                     │                    │                │
    │──SignUp_Click()─────►│                   │                │
    │                     │─── SendAsync() ───►│                │
    │                     │  tcs created        │                │
    │                     │  _pending["AAA"]=tcs│                │
    │                     │──────[AAA, SignUp JSON]─────────────►│
    │                     │  (caller suspends)  │                │
    │                     │                    │ Handle(SignUp) │
    │                     │                    │ MemberService  │
    │                     │◄─────[AAA, Member JSON]─────────────│
    │                     │  ReceiveLoopAsync   │                │
    │                     │  finds "AAA" in     │                │
    │                     │  _pending           │                │
    │                     │  tcs.SetResult()    │                │
    │◄── member added ────│  (caller resumes)   │                │
```

### Server-push broadcast (booking made by any client)

```
Client A            Client B           Service
    │                   │                 │
    │──[BookCourt]───────────────────────►│
    │                   │                 │  booking saved
    │                   │                 │  BroadcastAsync()
    │◄──[BookCourt reply, id=BBB]─────────│  (direct reply)
    │◄──[BookingBroadcast, id=""]─────────│  (broadcast)
    │   ReceiveLoop:    │◄────────────────│  (broadcast)
    │   id="" not in    │                 │
    │   _pending →      │ id="" not in    │
    │   PushReceived    │ _pending →      │
    │   Live Feed ✓     │ PushReceived    │
    │   Bookings ✓      │ Live Feed ✓     │
    │                   │ Bookings ✓      │
```

---

## 10. Threading Model

Understanding which code runs on which thread is important in a WPF application.

| Code | Thread | Reason |
|---|---|---|
| Button click handlers (`async void`) | **UI thread** | WPF event system |
| `await _client.SendAsync(...)` continuation | **UI thread** | WPF `SynchronizationContext` resumes on UI thread |
| `ReceiveLoopAsync` | **Thread-pool thread** | Started with `Task.Run(ReceiveLoopAsync)` |
| `PushReceived?.Invoke(response)` | **Thread-pool thread** | Called from receive loop |
| `Dispatcher.Invoke(...)` in `OnPushReceived` | Marshals back to **UI thread** | Required before touching WPF controls or `ObservableCollection` |

The critical rule: **WPF controls may only be read or written on the UI thread.**
Because `PushReceived` fires on a background thread, `OnPushReceived` uses
`Dispatcher.Invoke` to cross back to the UI thread before updating `_liveFeed`
or `_bookings`.
