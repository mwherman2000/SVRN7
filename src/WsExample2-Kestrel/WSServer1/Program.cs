using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

class Program // WSServer
{
    static readonly Dictionary<Guid, WebSocket> _connections = new();
    static readonly object _lock = new();

    static readonly HashSet<Task> _clientTasks = new();
    static readonly object _taskLock = new();

    static readonly Guid   _instanceId  = Guid.NewGuid();
    static readonly Guid   _mvid        = typeof(Program).Module.ModuleVersionId;
    static readonly string _appName     = typeof(Program).Assembly.GetName().Name ?? "";
    static readonly string _appFullName = typeof(Program).Assembly.GetName().FullName;
    static readonly string _appVersion  = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";

    static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan IdleTimeout       = TimeSpan.FromSeconds(15);
    static readonly TimeSpan WatchdogInterval  = TimeSpan.FromSeconds(5);

    const int MaxMessageBytes = 1 * 1024 * 1024;
    const int MaxConnections  = 100;

    static string Ts() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    static async Task<int> Main(string[] args)
    {
        string host = "localhost";
        int port = 7443;
        string path = "/didcommws";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host" when i + 1 < args.Length:
                    host = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out port) || port < 1 || port > 65535)
                    {
                        Console.Error.WriteLine($"{Ts()} Error: invalid port '{args[i]}'");
                        PrintUsage();
                        return 1;
                    }
                    break;
                case "--path" when i + 1 < args.Length:
                    path = args[++i];
                    if (!path.StartsWith('/'))
                        path = "/" + path;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"{Ts()} Error: unknown argument '{args[i]}'");
                    PrintUsage();
                    return 1;
            }
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(opts =>
            opts.ShutdownTimeout = TimeSpan.FromSeconds(15));

        WebApplication app = builder.Build();

        WebSocketOptions wsOptions = new()
        {
            KeepAliveInterval = KeepAliveInterval,
        };
        wsOptions.AllowedOrigins.Add($"http://{host}:{port}");
        app.UseWebSockets(wsOptions);

        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        app.MapGet("/health", () =>
        {
            string json = $$"""{"status":"ok","connections":{{ConnectionCount()}},"maxConnections":{{MaxConnections}},"instanceId":"{{_instanceId}}","appName":"{{_appName}}","appVersion":"{{_appVersion}}"}""";
            return Results.Content(json, "application/json");
        });

        app.Map(path, async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            if (ConnectionCount() >= MaxConnections)
            {
                Console.WriteLine($"{Ts()} Connection rejected — max connections ({MaxConnections}) reached");
                context.Response.StatusCode = 503;
                return;
            }
            WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
            Task clientTask = HandleClientAsync(ws, lifetime.ApplicationStopping);
            lock (_taskLock) _clientTasks.Add(clientTask);
            _ = clientTask.ContinueWith(t => { lock (_taskLock) _clientTasks.Remove(t); });
            await clientTask; // keeps Kestrel's request pipeline open for the connection lifetime
        });

        Console.WriteLine($"{Ts()} WSServer1 listening on http://{host}:{port}{path}");
        Console.WriteLine($"{Ts()} Health check:         http://{host}:{port}/health");
        Console.WriteLine($"{Ts()} Waiting for connections... (Ctrl+C to stop)");

        await app.RunAsync();

        // Graceful shutdown: close any remaining open sockets and wait for handlers
        WebSocket[] sockets;
        lock (_lock)
        {
            sockets = new WebSocket[_connections.Count];
            _connections.Values.CopyTo(sockets, 0);
        }
        if (sockets.Length > 0)
        {
            Console.WriteLine($"{Ts()} Closing {sockets.Length} active connection(s)...");
            foreach (WebSocket ws in sockets)
            {
                if (ws.State == WebSocketState.Open)
                {
                    using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "server shutting down", closeCts.Token); }
                    catch { }
                }
            }
        }

        Task[] pending;
        lock (_taskLock)
        {
            pending = new Task[_clientTasks.Count];
            _clientTasks.CopyTo(pending);
        }
        if (pending.Length > 0)
        {
            Console.WriteLine($"{Ts()} Waiting for {pending.Length} client(s) to disconnect...");
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { }
        }

        Console.WriteLine($"{Ts()} Server stopped.");
        return 0;
    }

    static async Task HandleClientAsync(WebSocket ws, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();
        SemaphoreSlim sendLock = new(1, 1);

        lock (_lock)
            _connections[id] = ws;

        Console.WriteLine($"{Ts()} [{id}] connected  ({ConnectionCount()} total)");

        DateTime lastReceived = DateTime.UtcNow;
        using CancellationTokenSource idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task watchdog = Task.Run(async () =>
        {
            try
            {
                while (!idleCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(WatchdogInterval, idleCts.Token);
                    if (DateTime.UtcNow - lastReceived > IdleTimeout)
                    {
                        Console.WriteLine($"{Ts()} [{id}] idle timeout ({IdleTimeout.TotalSeconds}s), closing");

                        string timeoutMsg = $$"""{"type":"timeout","instanceId":"{{_instanceId}}","appName":"{{_appName}}","appFullName":"{{_appFullName}}","mvid":"{{_mvid}}","appVersion":"{{_appVersion}}"}""";
                        using CancellationTokenSource sendCts = new(TimeSpan.FromSeconds(5));
                        await sendLock.WaitAsync(sendCts.Token);
                        try { await ws.SendAsync(Encoding.UTF8.GetBytes(timeoutMsg), WebSocketMessageType.Text, true, sendCts.Token); }
                        catch { }
                        finally { sendLock.Release(); }

                        using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "idle timeout", closeCts.Token); }
                        catch { }

                        try { await Task.Delay(TimeSpan.FromSeconds(5), idleCts.Token); }
                        catch (OperationCanceledException) { return; }
                        idleCts.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        byte[] buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !idleCts.Token.IsCancellationRequested)
            {
                using MemoryStream ms = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, idleCts.Token);
                    if (result.MessageType != WebSocketMessageType.Close)
                        ms.Write(buffer, 0, result.Count);

                    if (ms.Length > MaxMessageBytes)
                    {
                        Console.Error.WriteLine($"{Ts()} [{id}] message too large ({ms.Length} bytes), closing");
                        using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
                        try { await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", closeCts.Token); }
                        catch { }
                        return;
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.State == WebSocketState.CloseReceived)
                    {
                        using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token); }
                        catch { }
                    }
                    break;
                }

                lastReceived = DateTime.UtcNow;

                // ↓ application logic — replace with your message handler; keep sendLock guard
                string text = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"{Ts()} [{id}] recv: {text}");

                byte[] reply = Encoding.UTF8.GetBytes($">>> {text}");
                await sendLock.WaitAsync(ct);
                try { await ws.SendAsync(reply, WebSocketMessageType.Text, true, ct); }
                finally { sendLock.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Console.Error.WriteLine($"{Ts()} [{id}] WebSocket error ({ex.WebSocketErrorCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Ts()} [{id}] error: {ex}");
        }
        finally
        {
            idleCts.Cancel();
            await watchdog;

            if (ws.State == WebSocketState.Open)
            {
                using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", closeCts.Token); }
                catch { }
            }

            lock (_lock)
                _connections.Remove(id);

            Console.WriteLine($"{Ts()} [{id}] disconnected ({ConnectionCount()} remaining)");

            sendLock.Dispose();
            ws.Dispose();
        }
    }

    static int ConnectionCount()
    {
        lock (_lock)
            return _connections.Count;
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine("""
            Usage: WSServer1 [options]

            Options:
              --host <host>   Hostname or IP to listen on  (default: localhost)
              --port <port>   TCP port to listen on        (default: 7443)
              --path <path>   WebSocket URL path           (default: /didcommws)
              -h, --help      Show this help message
            """);
    }
}
