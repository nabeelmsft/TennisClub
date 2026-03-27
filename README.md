# Tennis Club Membership

A .NET 8 application for managing tennis club memberships and court bookings.

## Projects

| Project | Type | Description |
|---|---|---|
| `TennisClub.Shared` | Class Library | Shared models, messages, and payloads |
| `TennisClub.Service` | Console App | WebSocket server (listens on `ws://localhost:5000/`) |
| `TennisClub.Client` | WPF App | Desktop client UI |

## Features

- **Member sign-up** — register members with name and email
- **Court availability** — check which courts are free for a given date
- **Court booking** — book one of 3 courts across three evening slots (17:00, 18:00, 19:00)
- **Booking history** — view all bookings

## Architecture

The client and server communicate over a **WebSocket** connection. Every request is wrapped in a `WebSocketMessage` envelope carrying a `RequestId` (GUID) and a `MessageType`. The server echoes the same `RequestId` back in its `WebSocketResponse`, allowing the client to correlate async responses to their originating requests using a `ConcurrentDictionary<string, TaskCompletionSource<WebSocketResponse>>`.

## How to run

1. Start **`TennisClub.Service`** — the WebSocket server starts on `ws://localhost:5000/`
2. Start **`TennisClub.Client`** — the WPF window connects automatically on startup

> Data is held in-memory; it resets when the service restarts.

## Tech stack

- .NET 8, C# 12
- WPF (client UI)
- `System.Net.HttpListener` + `System.Net.WebSockets` (no third-party dependencies)
- `System.Text.Json` for serialization
