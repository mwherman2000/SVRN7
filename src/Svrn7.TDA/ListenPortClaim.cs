using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Svrn7.TDA;

/// <summary>
/// A listen socket bound and put into listening state in the pre-host block, then
/// handed to Kestrel via <c>ListenHandle</c> (docs/AGENTWALLET.md §D11, approach C).
///
/// Binding here — before the host is built and before the DID Document is
/// written — makes the port claim <b>atomic</b>: the socket is held for the
/// process lifetime, so two TDAs first-running concurrently cannot both take the
/// same port. The port is known before anything is persisted, so no post-bind
/// patch of the DID Document is needed.
/// </summary>
public sealed class ListenPortClaim : IDisposable
{
    /// <summary>The bound, listening socket. Its handle is passed to Kestrel.</summary>
    public Socket Socket { get; }

    /// <summary>The port actually claimed.</summary>
    public int Port { get; }

    private bool _disposed;

    private ListenPortClaim(Socket socket, int port)
    {
        Socket = socket;
        Port = port;
    }

    /// <summary>
    /// Claims a port. When <paramref name="allowAutoSelect"/> is true, tries
    /// <paramref name="basePort"/>, then <paramref name="basePort"/>+1 … up to
    /// <paramref name="span"/> candidates, returning the first that binds. When
    /// false, binds exactly <paramref name="basePort"/> or throws.
    /// </summary>
    /// <exception cref="IOException">No candidate port could be bound.</exception>
    public static ListenPortClaim Acquire(int basePort, int span, bool allowAutoSelect, ILogger? log = null)
    {
        var candidates = allowAutoSelect ? Math.Max(1, span) : 1;

        for (var i = 0; i < candidates; i++)
        {
            var port = basePort + i;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                // No SO_REUSEADDR: we want a genuine failure when the port is
                // already owned, so auto-select advances and a fixed port fails loudly.
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                socket.Listen(512);
                if (i > 0)
                    log?.LogInformation("ListenPortClaim: port {Base} busy, claimed {Port} instead.", basePort, port);
                return new ListenPortClaim(socket, port);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                socket.Dispose();
                if (i == candidates - 1)
                {
                    var tried = allowAutoSelect ? $"{basePort}..{basePort + candidates - 1}" : basePort.ToString();
                    throw new IOException(
                        $"Could not bind a listen port ({tried} in use). " +
                        (allowAutoSelect
                            ? "Widen --port-span or stop whatever holds those ports."
                            : "This identity is published on that port; free it or move the endpoint."),
                        ex);
                }
            }
        }

        throw new IOException("Unreachable: port claim loop exhausted."); // satisfies definite-return
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Socket.Dispose(); } catch { /* best-effort */ }
    }
}
