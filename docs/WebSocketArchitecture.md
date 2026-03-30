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
11. [Write-Operation Confirmation Gate](#11-write-operation-confirmation-gate)
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

---

## 11. Write-Operation Confirmation Gate

### The problem

As the application grows, any developer adding a new button could accidentally fire a
destructive server call without the user being asked to confirm. Putting a
`MessageBox.Show` in every write handler works, but it scatters the policy across the
codebase and makes it easy to forget.

### Design goals

1. Declare write operations **once**, in one place.
2. Intercept them **automatically** — new write handlers get confirmation for free.
3. Keep UI concerns (the `MessageBox`) out of the network/transport layer.

---

### Part 1 — Single source of truth (`MessageTypeExtensions.cs`, Shared project)

```csharp
private static readonly HashSet<MessageType> WriteOperations =
[
    MessageType.SignUp,
    MessageType.BookCourt
];

public static bool IsWriteOperation(this MessageType type) =>
    WriteOperations.Contains(type);

public static string GetDisplayName(this MessageType type) =>
    type switch
    {
        MessageType.SignUp   => "Register New Member",
        MessageType.BookCourt => "Book a Court",
        ...
    };
```

A `HashSet<MessageType>` is the registry. Adding a new write operation in the future
means adding one line to this set — nothing else needs to change anywhere.

The extension method `IsWriteOperation()` lives in the **Shared** project because the
rule "what is a write operation" is domain knowledge, not UI knowledge or transport
knowledge. Both layers can reference it without depending on each other.

`GetDisplayName()` provides a human-readable label used in confirmation dialogs and
log messages, keeping display strings co-located with the type they describe.

---

### Part 2 — Confirmation callback (`WebSocketClient.cs`, Client project)

```csharp
/// <summary>
/// Optional confirmation gate for write operations.
/// Return true  → the message is sent normally.
/// Return false → SendAsync throws OperationCanceledException.
/// </summary>
public Func<MessageType, Task<bool>>? ConfirmWriteAsync { get; set; }

public async Task<WebSocketResponse> SendAsync(WebSocketMessage message)
{
    if (message.Type.IsWriteOperation() && ConfirmWriteAsync is not null)
    {
        var confirmed = await ConfirmWriteAsync(message.Type);
        if (!confirmed)
            throw new OperationCanceledException(...);
    }
    // ... normal send path
}
```

`WebSocketClient` is a **transport layer** concern — it manages bytes on a wire. It
should not know what a `MessageBox` is. The `Func<MessageType, Task<bool>>` delegate
is a seam: the transport layer knows *when* to ask for confirmation (any write
operation), but it delegates *how* to whoever owns the client.

This is the **Strategy pattern** applied to a single method: swap the delegate and
you swap the confirmation behaviour without touching the transport code.
The `Task<bool>` return type makes the callback async-safe — a future implementation
could show a custom animated dialog and `await` user input.

---

### Part 3 — Wiring the UI (`MainWindow.xaml.cs`, Client project)

```csharp
_client.ConfirmWriteAsync = type =>
{
    var result = MessageBox.Show(
        $"You are about to perform a write operation:\n\n    •  {type.GetDisplayName()}\n\nDo you want to continue?",
        "Confirm Write Operation",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
    return Task.FromResult(result == MessageBoxResult.Yes);
};
```

The UI layer owns the *policy* (show a `MessageBox`) and assigns it to the transport
layer's callback property. `Task.FromResult(...)` wraps the synchronous `MessageBox`
result in a completed `Task<bool>` to satisfy the async delegate signature.

Because `ConfirmWriteAsync` is awaited inside `SendAsync`, and `SendAsync` is always
called from a button-click handler (which runs on the **UI thread**), the callback
itself also executes on the UI thread — `MessageBox.Show` is safe without any
`Dispatcher` marshalling.

---

### Part 4 — Handling cancellation in write handlers

When the user clicks **No**, `SendAsync` throws `OperationCanceledException`. Write
handlers catch it silently — it is not an error, it is a deliberate user choice:

```csharp
private async void SignUp_Click(object sender, RoutedEventArgs e)
{
    try
    {
        var response = await _client.SendAsync(...);
        // handle success
    }
    catch (OperationCanceledException) { /* user chose No — nothing to report */ }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "Error", ...); // genuine errors still surface
    }
}
```

Read handlers (`RefreshMembers_Click`, `CheckAvailability_Click`, etc.) do **not**
catch `OperationCanceledException` because `IsWriteOperation()` returns `false` for
them — `SendAsync` never throws for read operations.

---

### Confirmation behaviour at a glance

| Operation | Write? | Confirmation shown |
|---|---|---|
| Sign Up | ✅ | Yes — "Register New Member" |
| Book a Court | ✅ | Yes — "Book a Court" |
| Get Members | ❌ | No |
| Check Availability | ❌ | No |
| Get Bookings | ❌ | No |

---

### Extending the gate

To add a new write operation in the future:

1. Add the new `MessageType` value to the `WriteOperations` `HashSet` in
   `MessageTypeExtensions.cs`.
2. Add a display name entry to the `GetDisplayName` switch expression.
3. Add `catch (OperationCanceledException) { }` to the new handler.

No changes are needed in `WebSocketClient`, `MessageHandler`, or any existing handler.



---

## 12. Ping/Pong Heartbeats

### The Problem: Silent Drops

A WebSocket connection can die without either side knowing. Consider:

- A laptop goes to sleep mid-session.
- A home router drops idle connections after 30-60 s (NAT table expiry).
- A mobile device roams from Wi-Fi to cellular.

In all these cases the TCP connection is gone but the OS has not sent a FIN packet.
Both sides still *think* the socket is open. The server holds the object in memory
forever — a **zombie connection**.

### The Fix: Application-Level Ping/Pong

Every 10 seconds the server sends a `Ping` message.
The client replies with a `Pong` within 25 seconds.
If no Pong arrives the server closes its end and reclaims the slot.

### Why Not RFC-6455 Protocol-Level Ping Frames?

The WebSocket spec defines control frames (opcode 0x9/0xA) for this purpose.
The .NET `ClientWebSocket` does **not** expose an API to send or receive them.
Using a regular JSON message achieves the same result while staying entirely
inside the existing message pipeline.

### The Concurrent-Send Problem

Three code paths can write to the same `WebSocket` simultaneously:

| Writer | When |
|---|---|
| `HandleClientAsync` | Sending a response |
| `HeartbeatMonitor.SendPingAsync` | Every 10 s |
| `ConnectionManager.BroadcastAsync` | After every booking |

`WebSocket.SendAsync` throws if called concurrently on one instance.
The solution is a **per-socket `SemaphoreSlim(1,1)`** shared between all three writers.

### Server-Side Flow

```csharp
// HandleClientAsync in Program.cs

var (connectionId, sendLock) = connections.Add(ws);   // socket + its exclusive lock
var heartbeat = new HeartbeatMonitor(ws, sendLock, connectionId);
var heartbeatTask = Task.Run(() => heartbeat.RunAsync(cts.Token));

// In the receive loop — intercept Pong before routing:
if (message.Type == MessageType.Pong)
{
    heartbeat.RecordPong();   // Interlocked.Exchange on _lastPongTicks
    continue;                 // never reaches MessageHandler
}

// Response send — acquire the shared lock first:
await sendLock.WaitAsync();
try   { await ws.SendAsync(...); }
finally { sendLock.Release(); }

// Cleanup — cancel the heartbeat and wait before removing the connection:
cts.Cancel();
await heartbeatTask;
connections.Remove(connectionId);   // also disposes the SemaphoreSlim
```

### Client-Side Flow

```csharp
// WebSocketClient.ReceiveLoopAsync

if (response.Type == MessageType.Ping)
{
    _ = Task.Run(SendPongAsync);   // reply on a background thread
    continue;                      // skip _pending routing
}

// SendPongAsync uses the same _sendLock as SendAsync:
await _sendLock.WaitAsync(_cts.Token);
try   { await _ws.SendAsync(pong, ...); }
finally { _sendLock.Release(); }
```

### Why `long` Ticks Instead of `volatile DateTime`

`volatile` fields must be reference types or certain primitive types. `DateTime`
is a struct and is not volatile-compatible. Storing UTC ticks as `long` and using
`Interlocked` gives lock-free atomic read/write:

```csharp
private long _lastPongTicks = DateTime.UtcNow.Ticks;

public void RecordPong()
    => Interlocked.Exchange(ref _lastPongTicks, DateTime.UtcNow.Ticks);

// Inside RunAsync:
if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPongTicks) > Timeout.Ticks)
    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Heartbeat timeout", ...);
```

---

## 13. Reconnection Logic

### The Problem: Server Restarts

When the server restarts or the network blips, the client's `ReceiveLoopAsync`
exits. Without reconnection logic the user must restart the whole application.

### The Fix: Auto-Reconnect with Exponential Back-off

When the receive loop exits normally (not because `DisposeAsync` cancelled it),
it calls `ReconnectAsync`:

```csharp
// End of ReceiveLoopAsync:
if (!_cts.IsCancellationRequested)
    await ReconnectAsync();
```

Hammering a down server with immediate retries wastes resources and can worsen
an outage. Back-off gives the server time to recover:

| Attempt | Delay |
|---|---|
| 1 | 2 s |
| 2 | 4 s |
| 3 | 8 s |
| 4 | 16 s |
| 5 | 30 s (capped) |

```csharp
var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 30));
```

### Replacing the WebSocket Instance

`ClientWebSocket` is single-use — once it enters an `Aborted`/`Closed` state it
cannot be reconnected. `ReconnectAsync` disposes the dead instance and creates a
fresh one:

```csharp
_ws.Dispose();
_ws = new ClientWebSocket();                    // _ws is non-readonly for this reason
await _ws.ConnectAsync(new Uri(_url!), token);
_ = Task.Run(ReceiveLoopAsync);                // restart the receive loop
Reconnected?.Invoke();
```

### UI Feedback via Events

Two events decouple the reconnection logic from the UI layer:

```csharp
public event Action<int>? Reconnecting;   // fired at the start of each attempt
public event Action?      Reconnected;    // fired on success
```

Both fire on a thread-pool thread. `MainWindow.xaml.cs` marshals to the UI thread:

```csharp
_client.Reconnecting += attempt => Dispatcher.Invoke(() =>
    StatusText.Text = $"Connection lost — reconnecting… (attempt {attempt}/5)");
_client.Reconnected += () =>
    Dispatcher.Invoke(() => StatusText.Text = "Reconnected to server.");
```

### Exhausted Retries

If all five attempts fail, every pending `TaskCompletionSource` is faulted so
callers do not wait forever:

```csharp
var error = new Exception($"Could not reconnect after {MaxReconnectAttempts} attempts.");
foreach (var tcs in _pending.Values)
    tcs.TrySetException(error);
```

### Timeline

```
Client                              Server
  |                                   |
  |  Connection drops                 X   (restart / NAT timeout)
  |  ReceiveLoopAsync exits           |
  |  ReconnectAsync() called          |
  |                                   |
  |  [2 s] attempt 1                  |   (server still starting)
  |  ConnectAsync ─────────────────▶  X   (connection refused)
  |                                   |
  |  [4 s] attempt 2                  |   (server ready)
  |  ConnectAsync ─────────────────▶  |
  |             ◀── Upgrade ──────    |
  |  ReceiveLoopAsync restarted       |
  |  Reconnected?.Invoke()            |
  |  StatusText = "Reconnected"       |
```
