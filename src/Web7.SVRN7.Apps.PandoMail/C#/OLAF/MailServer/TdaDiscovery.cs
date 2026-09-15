using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Web7.SVRN7.Apps
{
    /// <summary>One TDA found by <see cref="TdaDiscovery.ScanAsync"/>.</summary>
    public sealed record DiscoveredTda(string Name, int Port, string Did);

    /// <summary>
    /// Finds locally running TDA processes and probes each one's actual listening
    /// port for identity (name/DID) via an unauthenticated Hello handshake.
    ///
    /// Deliberately process-based, not a port-range guess: finds candidate PIDs
    /// two ways — processes literally named "Svrn7.TDA" (the published apphost
    /// exe) and "dotnet" processes whose command line references
    /// "Svrn7.TDA.dll" (running via `dotnet Svrn7.TDA.dll ...`, which is how
    /// this TDA has been launched throughout development) — then resolves each
    /// PID's actual listening TCP port via GetExtendedTcpTable, the same OS data
    /// `netstat -ano` reads. This works for any --port/--port-base a TDA was
    /// started with, not just the default range, and needs no speculative
    /// connection attempts against ports nothing is listening on.
    /// </summary>
    public static class TdaDiscovery
    {
        private const string TdaApphostProcessName = "Svrn7.TDA";
        private const string DotnetProcessName      = "dotnet";
        private const string TdaDllMarker           = "Svrn7.TDA.dll";
        // Covers connect (HTTP/2 h2c upgrade, RFC 8441) + send Hello + receive the
        // Subscribed ack, as one budget. TdaMailClient's own ConnectAsync alone uses a
        // 5s timeout just for the connect step (cold h2c connections aren't instant) —
        // this needs to be at least that plus round-trip time for the Hello/ack exchange.
        private static readonly TimeSpan HelloProbeTimeout = TimeSpan.FromSeconds(8);

        public static async Task<List<DiscoveredTda>> ScanAsync(CancellationToken ct = default)
        {
            var results = new List<DiscoveredTda>();

            var ports = GetTdaListeningPorts();
            if (ports.Count == 0)
                return results;

            var probes = ports.Select(port => ProbeAsync(port, ct));
            var probeResults = await Task.WhenAll(probes);

            foreach (var found in probeResults)
                if (found is not null)
                    results.Add(found);

            return results.OrderBy(t => t.Port).ToList();
        }

        /// <summary>Opens a short-lived WebSocket to a candidate port, sends Hello, and
        /// reads name/DID from the Subscribed ack. Returns null on any failure — an
        /// unreachable or non-TDA listener on that port is an expected, silent miss,
        /// not an error worth surfacing during a scan. Public so a caller with an
        /// already-known port (e.g. PandoMail's --port override) can skip the process
        /// scan entirely and still get identity info for display before Authenticate.</summary>
        public static async Task<DiscoveredTda> ProbeAsync(int port, CancellationToken ct = default)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(HelloProbeTimeout);

            using var ws = new ClientWebSocket();
            ws.Options.HttpVersion       = new Version(2, 0);
            ws.Options.HttpVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher;

            try
            {
                using var http = new System.Net.Http.HttpClient(
                    new System.Net.Http.SocketsHttpHandler { EnableMultipleHttp2Connections = true });
                await ws.ConnectAsync(new Uri($"ws://localhost:{port}/localcomm-ws"), http, timeoutCts.Token);

                var hello = JsonSerializer.Serialize(new
                {
                    typ  = "application/didcomm-plain+json",
                    id   = "did:drn:svrn7.net/didcomm/msg/" + Guid.NewGuid().ToString("N"),
                    type = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Hello",
                    body = new
                    {
                        app        = "PandoMail",
                        instanceId = Guid.NewGuid().ToString(),
                        subscriptions = Array.Empty<object>()
                    }
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, timeoutCts.Token);

                var buffer = new byte[16 * 1024];
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, timeoutCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl) ||
                    !(typeEl.GetString() ?? "").EndsWith("/Subscribed", StringComparison.Ordinal))
                    return null;

                if (!root.TryGetProperty("body", out var body)) return null;
                var name = body.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var did  = body.TryGetProperty("did",  out var d) ? d.GetString()  ?? "" : "";
                if (string.IsNullOrEmpty(did)) return null;

                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "scan complete", CancellationToken.None); }
                catch { /* best-effort */ }

                return new DiscoveredTda(name, port, did);
            }
            catch
            {
                // Not a TDA, not reachable, or timed out — a normal scan outcome, not an error.
                return null;
            }
        }

        // ── PID → listening port, via the Windows TCP table (netstat -ano's data source) ──

        private static List<int> GetTdaListeningPorts()
        {
            var pids = new HashSet<int>(GetTdaProcessIds());
            if (pids.Count == 0) return new List<int>();

            var ports = new List<int>();
            foreach (var (pid, port) in EnumerateListeningTcpPorts())
                if (pids.Contains(pid))
                    ports.Add(port);
            return ports;
        }

        /// <summary>
        /// PIDs of both the published apphost ("Svrn7.TDA.exe") and any "dotnet.exe"
        /// process whose command line references Svrn7.TDA.dll. Command-line inspection
        /// needs WMI (Win32_Process) — Process.GetProcessesByName("dotnet") alone can't
        /// tell a TDA host apart from an unrelated dotnet-hosted process (build server,
        /// another app, etc.).
        /// </summary>
        private static IEnumerable<int> GetTdaProcessIds()
        {
            foreach (var p in Process.GetProcessesByName(TdaApphostProcessName))
                yield return p.Id;

            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{DotnetProcessName}.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                var commandLine = mo["CommandLine"] as string;
                if (!string.IsNullOrEmpty(commandLine) &&
                    commandLine.IndexOf(TdaDllMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    yield return Convert.ToInt32(mo["ProcessId"]);
                }
            }
        }

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public byte remotePort1;
            public byte remotePort2;
            public byte remotePort3;
            public byte remotePort4;
            public uint owningPid;

            public int LocalPort => (localPort1 << 8) + localPort2;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, uint reserved);

        /// <summary>Yields (pid, localPort) for every IPv4 TCP listener on the machine.</summary>
        private static IEnumerable<(int Pid, int Port)> EnumerateListeningTcpPorts()
        {
            int bufSize = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref bufSize, sort: true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);

            IntPtr buffer = Marshal.AllocHGlobal(bufSize);
            try
            {
                var ret = GetExtendedTcpTable(buffer, ref bufSize, sort: true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (ret != 0) yield break;

                int rowCount = Marshal.ReadInt32(buffer);
                int rowSize  = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    yield return ((int)row.owningPid, row.LocalPort);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
