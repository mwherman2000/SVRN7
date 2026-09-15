# WsExample2 (Kestrel) — Preferred Design

> **Preferred design over WsExample1.** Uses Kestrel (`Microsoft.NET.Sdk.Web`) instead of
> `HttpListener`: cross-platform, declarative routing, integrated shutdown lifecycle, and
> Kestrel-native WebSocket options (`AllowedOrigins`, aligned `ShutdownTimeout`).
> See [WsExample1](../WsExample1-HTTP/README.md) for the `HttpListener` reference design.

A .NET 8 WebSocket server/client example demonstrating production-quality patterns using Kestrel
and `System.Net.WebSockets`. No third-party NuGet packages.

## Features

**Server (WSServer1)**
- HTTP upgrade to WebSocket via Kestrel (`Microsoft.NET.Sdk.Web`)
- Declarative routing via `app.Map` / `app.MapGet` — no manual accept loop
- WebSocket ping/pong frames (RFC 6455 §5.5.2): `KeepAliveInterval` 10 s via `WebSocketOptions`
- `AllowedOrigins` restricts browser WebSocket clients to same-origin (`http://{host}:{port}`)
- Host `ShutdownTimeout` 15 s aligned with 10 s cleanup budget
- Connection registry: `Dictionary<Guid, WebSocket>` keyed by per-connection GUID
- Echo: replies to every message with `>>> {original text}`
- Idle timeout (15 s): sends `{"type":"timeout",...}` then performs a graceful WebSocket close
- Health check endpoint: `GET /health` returns live connection stats as JSON
- Max 100 concurrent connections; returns HTTP 503 when the limit is reached
- Graceful shutdown: broadcasts close frames to all clients, waits up to 10 s for clean disconnect
- Per-connection `SemaphoreSlim` prevents concurrent-send frame corruption

**Client (WSClient1)**
- Sends `{"type":"attach",...}` on connect and `{"type":"detach",...}` on `bye`
- Recognises `{"type":"timeout"}` from the server and prints a human-readable reconnect message
- Auto-reconnects up to 10 × 1 s on connection loss
- 5 s connect timeout (distinguishes user Ctrl+C from a hung server)
- Queues messages typed while disconnected; drains them automatically after reconnect
- Ctrl+C performs a clean WebSocket close before exiting

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Quick Start

Open two terminals in the solution root:

```shell
# Terminal 1 — server
dotnet run --project WSServer1
```

```shell
# Terminal 2 — client
dotnet run --project WSClient1
```

Type any text in the client terminal and press Enter. The server echoes it back prefixed with `>>> `.

Special client commands:

| Input | Action |
|---|---|
| `bye` | Sends detach message, closes connection cleanly |
| `crash` | Exits the client process immediately (tests server handling) |
| Ctrl+C | Sends close frame, exits |
| Empty line | Disconnects (without detach) |

## Configuration

### Server

```
dotnet run --project WSServer1 -- [options]

  --host <host>   Hostname or IP to listen on  (default: localhost)
  --port <port>   TCP port                      (default: 7443)
  --path <path>   WebSocket URL path            (default: /didcommws)
  -h, --help
```

### Client

```
dotnet run --project WSClient1 -- [options]

  --url <url>   WebSocket server URL  (default: ws://localhost:7443/didcommws)
  -h, --help
```

## Health Check

```shell
curl http://localhost:7443/health
```

```json
{
  "status": "ok",
  "connections": 1,
  "maxConnections": 100,
  "instanceId": "a1b2c3d4-e5f6-...",
  "appName": "WSServer1",
  "appVersion": "1.0.0"
}
```

## Message Protocol

All protocol messages are UTF-8 JSON text frames.

### attach — client → server, sent immediately on connect

```json
{
  "type": "attach",
  "instanceId": "a1b2c3d4-...",
  "appName": "WSClient1",
  "appFullName": "WSClient1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "mvid": "b2c3d4e5-...",
  "appVersion": "1.0.0"
}
```

### detach — client → server, sent when user types `bye`

Same fields as `attach` with `"type": "detach"`.

### timeout — server → client, sent before closing an idle connection

```json
{
  "type": "timeout",
  "instanceId": "c3d4e5f6-...",
  "appName": "WSServer1",
  "appFullName": "WSServer1, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
  "mvid": "d4e5f6a7-...",
  "appVersion": "1.0.0"
}
```

### Field glossary

| Field | Description |
|---|---|
| `instanceId` | `Guid.NewGuid()` — unique per process run |
| `mvid` | `Module.ModuleVersionId` — unique per build output |
| `appVersion` | `AssemblyInformationalVersion` — carries git hash when SourceLink is enabled |
| `appFullName` | Fully qualified assembly name including version and culture |

## Project Layout

```
WsExample2.sln
WSServer1/
  WSServer1.csproj
  Program.cs
WSClient1/
  WSClient1.csproj
  Program.cs
docs/
  ARCHITECTURE.md     ← concurrency model, connection lifecycle, all 15 best practices
CLAUDE.md             ← build commands and conventions for Claude Code
README.md
llms.txt
```
