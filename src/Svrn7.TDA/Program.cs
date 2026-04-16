using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Svrn7.Society;
using Svrn7.TDA;

// ── Web 7.0 Trusted Digital Assistant (TDA) — Console App Entry Point ────────
//
// Derived from: Citizen/Society Trusted Digital Assistant (Host) — DSA 0.24 Epoch 0 (PPML).
//
// Runtime: .NET 8 console app using Generic Host + Kestrel HTTP/2 + mTLS.
// Single inbound surface: POST /didcomm (KestrelListenerService).
// No gRPC. No public REST API. Closed TDA-to-TDA ecosystem.
//
// Startup sequence (matches DSA 0.24 derivation chain):
//   1.  AddSvrn7Society()     — full SVRN7 stack (driver, stores, DIDComm, resolvers)
//   2.  AddSvrn7Tda()         — TDA Host: IMemoryCache, $SVRN7, LobeManager,
//                               RunspacePoolManager, Switchboard, KestrelListenerService
//   3.  UseConsoleLifetime()  — SIGTERM / Ctrl-C graceful shutdown
//   4.  host.RunAsync()       — blocks until shutdown

var host = Host.CreateDefaultBuilder(args)
    .UseConsoleLifetime()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddConsole();
    })
    .ConfigureServices((ctx, services) =>
    {
        // ── 1. SVRN7 Society stack ────────────────────────────────────────────
        // Derived from the SVRN7 LOBE (inside Agent 1 Runspace) — DSA 0.24.
        services.AddSvrn7Society(opts =>
        {
            // In production, load these from environment variables or a secrets manager.
            // These defaults are for development/test only.
            opts.SocietyDid                      = ctx.Configuration["Svrn7:SocietyDid"]
                                                   ?? "did:drn:alpha.svrn7.net";
            opts.FederationDid                   = ctx.Configuration["Svrn7:FederationDid"]
                                                   ?? "did:drn:foundation.svrn7.net";
            opts.Svrn7DbPath                     = ctx.Configuration["Svrn7:DbPath"]
                                                   ?? "svrn7.db";
            opts.DidsDbPath                      = ctx.Configuration["Svrn7:DidsDbPath"]
                                                   ?? "svrn7-dids.db";
            opts.VcsDbPath                       = ctx.Configuration["Svrn7:VcsDbPath"]
                                                   ?? "svrn7-vcs.db";
            opts.InboxDbPath                     = ctx.Configuration["Svrn7:InboxDbPath"]
                                                   ?? "svrn7-inbox.db";
            opts.SchemasDbPath                   = ctx.Configuration["Svrn7:SchemasDbPath"]
                                                   ?? "svrn7-schemas.db";
            opts.SocietyMessagingPrivateKeyEd25519 = Array.Empty<byte>(); // supplied at runtime
        });

        // Background services from Svrn7.Society (VC expiry, Merkle auto-sign).
        services.AddSvrn7SocietyBackgroundServices();

        // ── 2. TDA Host: five Critical DSA 0.24 components ───────────────────
        services.AddSvrn7Tda(opts =>
        {
            opts.SocietyDid                        = ctx.Configuration["Tda:SocietyDid"]
                                                     ?? "did:drn:alpha.svrn7.net";
            opts.SocietyMessagingPrivateKeyEd25519 = Array.Empty<byte>(); // supplied at runtime
            opts.ListenPort                        = int.Parse(
                                                     ctx.Configuration["Tda:ListenPort"] ?? "8443");
            opts.TlsCertificatePath                = ctx.Configuration["Tda:TlsCertPath"];
            opts.TlsCertificatePassword            = ctx.Configuration["Tda:TlsCertPassword"];
            opts.RequireMutualTls                  = bool.Parse(
                                                     ctx.Configuration["Tda:RequireMutualTls"] ?? "true");
            opts.AcceptSelfSignedPeerCertificates  = bool.Parse(
                                                     ctx.Configuration["Tda:AcceptSelfSigned"] ?? "false");
            opts.MinRunspaces                      = 2;
            opts.MaxRunspaces                      = 0; // default: ProcessorCount × 2
            opts.LobesConfigPath                   = ctx.Configuration["Tda:LobesConfigPath"]
                                                     ?? "./lobes/lobes.config.json";
        });
    })
    .Build();

// ── Startup banner ────────────────────────────────────────────────────────────
{
    var cfg     = host.Services.GetRequiredService<IConfiguration>();
    var driver  = host.Services.GetRequiredService<Svrn7.Society.ISvrn7SocietyDriver>();

    var version = typeof(Program).Assembly
                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                      ?.InformationalVersion
                  ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
                  ?? "0.0.0";

    // Federation and society data — may be null on first run before initialisation.
    var federation = await driver.GetFederationAsync();
    var societies  = await driver.GetAllSocietiesAsync();
    var activeSocietyCount = societies.Count(s => s.IsActive);

    const string hr = "────────────────────────────────────────────────────────────────────────────────";
    Console.WriteLine(hr);
    Console.WriteLine($"  SVRN7 Trusted Digital Assistant (TDA)  v{version}");
    Console.WriteLine($"  Web 7.0 Foundation — https://svrn7.net");
    Console.WriteLine(hr);
    Console.WriteLine($"  Started     : {DateTimeOffset.Now.ToString("F")}");
    Console.WriteLine($"  Executable  : {Environment.ProcessPath ?? "(unknown)"}");
    Console.WriteLine($"  Runtime     : {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"  OS          : {RuntimeInformation.OSDescription}");
    Console.WriteLine(hr);
    Console.WriteLine($"  Society DID : {cfg["Svrn7:SocietyDid"] ?? cfg["Tda:SocietyDid"] ?? "(not configured)"}");
    Console.WriteLine($"  Listen port : {cfg["Tda:ListenPort"] ?? "8443"}");
    Console.WriteLine($"  LOBEs       : {cfg["Tda:LobesConfigPath"] ?? "./lobes/lobes.config.json"}");
    Console.WriteLine(hr);
    if (federation is not null)
    {
        Console.WriteLine($"  Federation  : {federation.FederationName}  ({federation.Did})");
        Console.WriteLine($"  Supply      : {federation.TotalSupplyGrana / 1_000_000m:N6} SVRN7  ({federation.TotalSupplyGrana:N0} grana)");
        Console.WriteLine($"  Epoch       : {driver.GetCurrentEpoch()}");
        Console.WriteLine($"  Societies   : {societies.Count} registered  ({activeSocietyCount} active)");
    }
    else
    {
        Console.WriteLine($"  Federation  : (not yet initialised — run Invoke-Web7FederationInit)");
        Console.WriteLine($"  Societies   : (not yet initialised)");
    }
    Console.WriteLine(hr);
    Console.WriteLine();
}

await host.RunAsync();
