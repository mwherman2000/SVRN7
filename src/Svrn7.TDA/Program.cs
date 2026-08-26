using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Svrn7.Core;
using Svrn7.Core.Models;
using Svrn7.Identity;
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
//                               IsolatedRunspaceFactory, Switchboard, KestrelListenerService
//   3.  UseConsoleLifetime()  — SIGTERM / Ctrl-C graceful shutdown
//   4.  host.RunAsync()       — blocks until shutdown

// ── Command-line arguments ────────────────────────────────────────────────────
// --port <n>       TCP/IP port to listen on (required — no default).
//                  Databases are stored under "<BaseDir>/{port}/mem/".
//                  LOBEs are loaded from   "<BaseDir>/{port}/lobes/".
// --name <string>  Human-readable name for this TDA (required).
//                  Stored as Svrn7Name in the Wanderer DIDDocument on first run.
// --url <url>      Base URL advertised in the Wanderer DID Document service
//                  endpoint (scheme + host, no trailing slash).
//                  Default: http://localhost
//                  Full endpoint stored: <url>:{port}/didcomm
// --federationdomain <domain>
//                  Bare domain used to auto-discover the Federation TDA DIDComm endpoint
//                  via drn.directory DNS at startup (e.g. "svrn7.net").
//                  Equivalent to setting Tda:FederationDomain in appsettings.json.
//                  Discovered URL is exposed as $SVRN7.FederationEndpointUrl in LOBEs.
//                  Leave unset for testnet / manual endpoint configuration.
// --reset          Delete all databases and agent-identity.json for this port before
//                  starting, forcing a clean first-run Wanderer bootstrap.
// --jaeger         Export DIDComm pipeline traces to Jaeger (OTLP/gRPC) instead of
//                  the console. Default endpoint: http://localhost:4317.
// --jaeger-endpoint <url>
//                  OTLP/gRPC endpoint to use with --jaeger. Implies --jaeger.
// --help           Display this help and exit.

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        SVRN7 Trusted Digital Assistant (TDA)
        Web 7.0 Foundation — https://svrn7.net

        Usage:
          Svrn7.TDA --port <n> --name <string> [--url <url>] [--reset]
                    [--jaeger [--jaeger-endpoint <url>]] [--help]

        Parameters:
          --port <n>        (required) TCP/IP port this TDA listens on.
                            Databases are stored under <BaseDir>/{port}/mem/.
                            LOBEs       are loaded from <BaseDir>/{port}/lobes/.

          --name <string>   (required) Human-readable name for this TDA instance.
                            Stored as Svrn7Name in the Wanderer DID Document on
                            first run.

          --url <url>       Base URL advertised in the Wanderer DID Document
                            service endpoint (scheme + host, no trailing slash).
                            Default: http://localhost
                            Full endpoint stored: <url>:{port}/didcomm

          --federationdomain <domain>
                            Bare domain to auto-discover the Federation TDA endpoint
                            via drn.directory DNS at startup. Example: "svrn7.net"
                            queries "federation.svrn7.net.drn.directory" for a TXT
                            record containing the Federation DIDComm endpoint URL.
                            Discovered URL exposed as $SVRN7.FederationEndpointUrl.
                            Also configurable via Tda:FederationDomain in appsettings.

          --reset           Delete all databases and agent-identity.json for this
                            port before starting, forcing a clean first-run
                            Wanderer bootstrap. Use with caution — irreversible.

          --jaeger          Export DIDComm pipeline traces to Jaeger via OTLP/gRPC
                            instead of the console exporter. Default endpoint:
                            http://localhost:4317 (Jaeger's native OTLP receiver).

          --jaeger-endpoint <url>
                            OTLP/gRPC endpoint to use with --jaeger. Implies --jaeger.

          --help | -h       Display this help and exit.
        """);
    Environment.Exit(0);
}

int port;
{
    var portIdx = Array.IndexOf(args, "--port");
    if (portIdx < 0 || portIdx + 1 >= args.Length || !int.TryParse(args[portIdx + 1], out int p))
    {
        Console.Error.WriteLine("ERROR: --port <n> is required.");
        Environment.Exit(1);
        port = 0; // unreachable — satisfies definite assignment
    }
    else
    {
        port = p;
    }
}

string tdaName;
{
    var nameIdx = Array.IndexOf(args, "--name");
    if (nameIdx < 0 || nameIdx + 1 >= args.Length || string.IsNullOrWhiteSpace(args[nameIdx + 1]))
    {
        Console.Error.WriteLine("ERROR: --name <string> is required.");
        Environment.Exit(1);
        tdaName = string.Empty; // unreachable — satisfies definite assignment
    }
    else
    {
        tdaName = args[nameIdx + 1];
    }
}

string tdaUrl;
{
    var urlIdx = Array.IndexOf(args, "--url");
    tdaUrl = urlIdx >= 0 && urlIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[urlIdx + 1])
        ? args[urlIdx + 1].TrimEnd('/')
        : "http://localhost";
}

string federationDomainArg;
{
    var fdIdx = Array.IndexOf(args, "--federationdomain");
    federationDomainArg = fdIdx >= 0 && fdIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[fdIdx + 1])
        ? args[fdIdx + 1].Trim()
        : string.Empty;
}

try
{

bool forceReset = Array.IndexOf(args, "--reset") >= 0;
if (forceReset)
{
    var memDir = Path.Combine(AppContext.BaseDirectory, port.ToString(), "mem");
    if (Directory.Exists(memDir))
    {
        foreach (var f in Directory.GetFiles(memDir))
            DeleteWithRetry(f);
        Console.WriteLine($"--reset: deleted all files in {memDir}");
    }
}

string? jaegerEndpointArg;
{
    var jeIdx = Array.IndexOf(args, "--jaeger-endpoint");
    jaegerEndpointArg = jeIdx >= 0 && jeIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[jeIdx + 1])
        ? args[jeIdx + 1].Trim()
        : null;
}
bool useJaeger = Array.IndexOf(args, "--jaeger") >= 0 || jaegerEndpointArg is not null;
var jaegerEndpoint = jaegerEndpointArg ?? "http://localhost:4317";

var host = Host.CreateDefaultBuilder(args)
    .UseConsoleLifetime()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Debug); // MWH
        logging.AddSimpleConsole(opts =>
        {
            opts.TimestampFormat = "HH:mm:ss.fff ";
            opts.UseUtcTimestamp = true;
            opts.SingleLine      = false;
        });
    })
    .ConfigureServices((ctx, services) =>
    {
        // ── 0. OpenTelemetry tracing — console exporter ───────────────────────
        // Registered first so its hosted service builds the TracerProvider (and
        // registers the process-wide ActivityListener) before any hosted service
        // that accepts traffic — KestrelListenerService, SwitchboardHostedService —
        // gets a chance to start. Otherwise an early didcomm.receive/dispatch/invoke
        // span racing the TracerProvider's own startup would be silently dropped
        // (Source.StartActivity returns null until a listener is registered).
        // Traces the DIDComm pipeline end to end: KestrelListenerService.HandleInboundAsync /
        // ProcessWebSocketMessageAsync (didcomm.receive) → LiteInboxStore's didcomm.storage
        // spans (Svrn7.Society/MsgStore.cs) → DIDCommMessageSwitchboard's didcomm.dispatch /
        // lobe.import / didcomm.invoke / didcomm.deliver spans (Svrn7Telemetry.cs, Svrn7.Core).
        // --jaeger ships spans to Jaeger via OTLP/gRPC (Jaeger's native OTLP receiver —
        // the standalone OpenTelemetry.Exporter.Jaeger package was retired years ago).
        // Default: console exporter (prints each span as it completes).
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName:       Svrn7Telemetry.SourceName,
                serviceVersion:    Svrn7Telemetry.SourceVersion,
                serviceInstanceId: $"{tdaName}:{port}"))
            .WithTracing(tracing =>
            {
                // ASP.NET Core creates its own ambient (unsampled, since no ASP.NET
                // Core instrumentation is registered) Activity per inbound request.
                // The default ParentBasedSampler would inherit that "don't record"
                // decision onto didcomm.receive (its child). AlwaysOnSampler forces
                // every Svrn7.TDA span to record regardless of ambient parent state.
                tracing.SetSampler(new AlwaysOnSampler())
                       .AddSource(Svrn7Telemetry.SourceName)
                       .AddSource(DIDDocumentService.ActivitySource.Name);

                if (useJaeger)
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(jaegerEndpoint));
                else
                    tracing.AddConsoleExporter();
            });

        // ── 1. SVRN7 Society stack ────────────────────────────────────────────
        // Derived from the SVRN7 LOBE (inside Agent 1 Runspace) — DSA 0.24.
        services.AddSvrn7Society(opts =>
        {
            // In production, load these from environment variables or a secrets manager.
            // These defaults are for development/test only.
            opts.SocietyDid                        = ctx.Configuration["Svrn7:SocietyDid"]   ?? string.Empty;
            opts.FederationDid                     = ctx.Configuration["Svrn7:FederationDid"] ?? string.Empty;
            opts.Svrn7DbPath                       = ResolvePath(ctx.Configuration["Svrn7:DbPath"],         "svrn7.db",        port);
            opts.DidsDbPath                        = ResolvePath(ctx.Configuration["Svrn7:DidsDbPath"],     "svrn7-dids.db",   port);
            opts.VcsDbPath                         = ResolvePath(ctx.Configuration["Svrn7:VcsDbPath"],      "svrn7-vcs.db",    port);
            opts.MsgDbPath                         = ResolvePath(ctx.Configuration["Svrn7:MsgDbPath"],      "svrn7-msg.db",    port);
            opts.SchemasDbPath                     = ResolvePath(ctx.Configuration["Svrn7:SchemasDbPath"],  "svrn7-schemas.db",port);
            opts.SocietyMessagingPrivateKeyEd25519 = []; // supplied at runtime
        });

        // Background services from Svrn7.Society (VC expiry, Merkle auto-sign).
        services.AddSvrn7SocietyBackgroundServices();

        // ── 2. TDA Host: five Critical DSA 0.24 components ───────────────────
        services.AddSvrn7Tda(opts =>
        {
            opts.SocietyDid                        = ctx.Configuration["Tda:SocietyDid"] ?? string.Empty;
            opts.SocietyMessagingPrivateKeyEd25519 = []; // supplied at runtime
            opts.ListenPort                        = port;
            opts.Role                              = Svrn7Role.Wanderer;
            opts.TlsCertificatePath                = ctx.Configuration["Tda:TlsCertPath"];
            opts.TlsCertificatePassword            = ctx.Configuration["Tda:TlsCertPassword"];
            opts.RequireMutualTls                  = bool.Parse(
                                                     ctx.Configuration["Tda:RequireMutualTls"] ?? "true");
            opts.AcceptSelfSignedPeerCertificates  = bool.Parse(
                                                     ctx.Configuration["Tda:AcceptSelfSigned"] ?? "false");
            opts.MinRunspaces                      = 2;
            opts.MaxRunspaces                      = 0; // default: ProcessorCount × 2
            opts.LobesConfigPath                   = ctx.Configuration["Tda:LobesConfigPath"]
                                                     ?? Path.Combine(AppContext.BaseDirectory, "lobes", "lobes.config.json");
            opts.ParentTdaDid                      = ctx.Configuration["Tda:ParentTdaDid"]         ?? string.Empty;
            opts.ParentTdaEndpointUrl              = ctx.Configuration["Tda:ParentTdaEndpointUrl"] ?? string.Empty;
            opts.FederationDomain                  = !string.IsNullOrEmpty(federationDomainArg)
                                                     ? federationDomainArg
                                                     : ctx.Configuration["Tda:FederationDomain"] ?? string.Empty;
        });
    })
    .Build();

// Force the TracerProvider to build now (registers the process-wide ActivityListener)
// before any DID Document operations run below. Bootstrap runs between .Build() and
// .RunAsync() — before hosted services start — so without this, DIDDocumentService's
// Activity spans for the Wanderer's own first-run identity creation would be silently
// dropped (StartActivity returns null with no listener registered yet), the same race
// the didcomm.* spans are protected against by registering OpenTelemetry first above.
host.Services.GetRequiredService<TracerProvider>();

var driver  = host.Services.GetRequiredService<ISvrn7SocietyDriver>();
var tdaOpts = host.Services.GetRequiredService<IOptions<TdaOptions>>().Value;

// ── First-run bootstrap ───────────────────────────────────────────────────────
// On a fresh install (empty DID registry), auto-generate a Wanderer identity:
// secp256k1 key pair, DID derived from the public key, DID Document stored in
// svrn7-dids.db, and key material persisted to <port>/mem/agent-identity.json.
var identityPath = Path.Combine(AppContext.BaseDirectory, port.ToString(), "mem", "agent-identity.json");
string? agentDid  = null;
string? svrn7Name = null;
bool    isFirstRun;

if (await driver.DidRegistry.CountAsync() == 0)
{
    isFirstRun = true;
    var kp          = driver.GenerateSecp256k1KeyPair();
    var kaKp        = driver.GenerateX25519KeyPair();
    var genesisHash = await driver.Blake3HexAsync(Convert.FromHexString(kp.PublicKeyHex));
    agentDid        = $"did:drn:wanderer.svrn7.net/agent/1.0/{genesisHash}";
    svrn7Name       = tdaName;

    var didDoc = driver.CreateDidDocument(agentDid, kp.PublicKeyHex, "drn",
                     $"{tdaUrl}:{port}/didcomm", Svrn7Role.Wanderer, svrn7Name,
                     x25519PublicKeyHex: kaKp.PublicKeyHex);
    await driver.CreateDidAsync(didDoc);

    await File.WriteAllTextAsync(identityPath,
        JsonSerializer.Serialize(new
        {
            did                  = agentDid,
            publicKeyHex         = kp.PublicKeyHex,
            privateKeyHex        = Convert.ToHexString(kp.PrivateKeyBytes).ToLowerInvariant(),
            x25519PublicKeyHex   = kaKp.PublicKeyHex,
            x25519PrivateKeyHex  = Convert.ToHexString(kaKp.PrivateKeyBytes).ToLowerInvariant(),
            role                 = "Wanderer",
            createdAt            = DateTimeOffset.UtcNow.ToString("O"),
        }, new JsonSerializerOptions { WriteIndented = true }));

    tdaOpts.AgentKeyAgreementPrivateKey = kaKp.PrivateKeyBytes.ToArray();
    tdaOpts.AgentSigningPrivateKey      = kp.PrivateKeyBytes.ToArray();
    kp.ZeroPrivateKey();
    kaKp.ZeroPrivateKey();
}
else
{
    isFirstRun = false;
    if (File.Exists(identityPath))
    {
        var json   = await File.ReadAllTextAsync(identityPath);
        var elem   = JsonSerializer.Deserialize<JsonElement>(json);
        agentDid   = elem.GetProperty("did").GetString();
        var result = await driver.DidRegistry.ResolveAsync(agentDid!);
        svrn7Name  = result.Document?.Svrn7Name;

        // Restore X25519 key agreement private key for JWE decryption.
        if (elem.TryGetProperty("x25519PrivateKeyHex", out var kaHex) && kaHex.GetString() is { Length: > 0 } kaHexStr)
            tdaOpts.AgentKeyAgreementPrivateKey = Convert.FromHexString(kaHexStr);

        // Restore secp256k1 signing private key for outbound SignThenEncrypt.
        if (elem.TryGetProperty("privateKeyHex", out var skHex) && skHex.GetString() is { Length: > 0 } skHexStr)
            tdaOpts.AgentSigningPrivateKey = Convert.FromHexString(skHexStr);

        // Restore parent TDA wiring from identity file if not already set via config/env.
        if (string.IsNullOrEmpty(tdaOpts.ParentTdaDid)
            && elem.TryGetProperty("parentTdaDid", out var pDid))
            tdaOpts.ParentTdaDid = pDid.GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(tdaOpts.ParentTdaEndpointUrl)
            && elem.TryGetProperty("parentTdaEndpointUrl", out var pUrl))
            tdaOpts.ParentTdaEndpointUrl = pUrl.GetString() ?? string.Empty;
    }
}

// Publish runtime values into TdaOptions so Svrn7RunspaceContext
// (constructed lazily during host.StartAsync) picks them up via the factory.
tdaOpts.LocalDid           = agentDid ?? tdaOpts.SocietyDid;
tdaOpts.ServiceEndpointUrl = $"{tdaUrl}:{port}/didcomm";
tdaOpts.AgentIdentityPath  = identityPath;

// ── drn.directory Federation endpoint discovery ───────────────────────────────
// If a FederationDomain is configured and no endpoint is already known, query
// drn.directory DNS to discover the Federation TDA's DIDComm endpoint.
// Result is stored in FederationEndpointUrl and exposed as $SVRN7.FederationEndpointUrl.
if (!string.IsNullOrEmpty(tdaOpts.FederationDomain) && string.IsNullOrEmpty(tdaOpts.FederationEndpointUrl))
{
    var discovered = await DrnDirectory.GetFederationEndpointAsync(tdaOpts.FederationDomain);
    if (discovered is not null)
        tdaOpts.FederationEndpointUrl = discovered;
}

// ── Startup banner ────────────────────────────────────────────────────────────
{
    var rawVersion = typeof(Program).Assembly
                         .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                         ?.InformationalVersion
                     ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
                     ?? "0.0.0";
    // Strip SemVer build metadata (git commit hash appended by the .NET SDK: "0.8.0+e542da3...")
    var version = rawVersion.Contains('+') ? rawVersion[..rawVersion.IndexOf('+')] : rawVersion;

    // ── LOBE / cmdlet counts (read descriptors directly — LobeManager not started yet) ──
    var lobesConfigPath = tdaOpts.LobesConfigPath;
    var lobeDir         = Path.GetDirectoryName(Path.GetFullPath(lobesConfigPath)) ?? AppContext.BaseDirectory;
    var lobeConfig      = File.Exists(lobesConfigPath)
        ? JsonSerializer.Deserialize<LobeConfig>(
              File.ReadAllText(lobesConfigPath),
              LobeDescriptor.JsonOpts)
          ?? new LobeConfig()
        : new LobeConfig();
    var descriptors = Directory.Exists(lobeDir)
        ? Directory.GetFiles(lobeDir, "*.lobe.json", SearchOption.AllDirectories)
              .Select(LobeDescriptor.LoadFromFile)
              .Where(d => d is not null)
              .Cast<LobeDescriptor>()
              .ToList()
        : [];
    var totalProtocols = descriptors.Sum(d => d.Protocols.Count);
    var totalCmdlets   = descriptors.Sum(d => d.Cmdlets.Count);

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
    Console.WriteLine($"  CWD         : {Environment.CurrentDirectory}");
    Console.WriteLine($"  Runtime     : {RuntimeInformation.FrameworkDescription}");
    Console.WriteLine($"  OS          : {RuntimeInformation.OSDescription}");
    Console.WriteLine(hr);
    Console.WriteLine($"  TDA Name    : {svrn7Name ?? "(unknown)"}");
    Console.WriteLine($"  TDA Role    : {tdaOpts.Role}");
    Console.WriteLine($"  Initialized : {(isFirstRun ? "no — new Wanderer identity created" : "yes — using existing identity")}");
    Console.WriteLine($"  Agent DID   : {agentDid ?? tdaOpts.SocietyDid}");
    Console.WriteLine($"  Listen port : {port}");
    Console.WriteLine($"  Fed Domain  : {(!string.IsNullOrEmpty(tdaOpts.FederationDomain)    ? tdaOpts.FederationDomain    : "(not configured — use --federationdomain)")}");
    Console.WriteLine($"  Fed Endpoint: {(!string.IsNullOrEmpty(tdaOpts.FederationEndpointUrl) ? tdaOpts.FederationEndpointUrl : "(not resolved — no drn.directory record found)")}");

    // JIT = all discovered descriptors whose module is not in the eager list.
    var eagerModuleNames = lobeConfig.Eager
        .Select(f => Path.GetFileNameWithoutExtension(f))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var jitDescriptors  = descriptors.Where(d => !eagerModuleNames.Contains(d.Lobe.Name)).ToList();
    Console.WriteLine($"  LOBEs       : {lobeConfig.Eager.Length} eager  {jitDescriptors.Count} JIT  ({totalProtocols} protocols  {totalCmdlets} cmdlets)");
    if (lobeConfig.Eager.Length > 0)
    {
        var eagerNames = lobeConfig.Eager.Select(f => Path.GetFileNameWithoutExtension(f));
        Console.WriteLine($"    Eager     : {string.Join("  ", eagerNames)}");
    }
    if (jitDescriptors.Count > 0)
    {
        Console.WriteLine($"    JIT       : {string.Join("  ", jitDescriptors.Select(d => d.Lobe.Name))}");
    }
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
        Console.WriteLine($"  Federation  : (not yet initialised — see FEDERATIONDEBUG.ps1 §E.0 to generate keys and POST federation/1.0/init to :{port}/didcomm)");
        Console.WriteLine($"  Societies   : (not yet initialised — see FEDERATIONDEBUG.ps1 §E.2 to register the first society)");
    }
    Console.WriteLine(hr);
    Console.WriteLine();
}

await host.RunAsync();

}
catch (Exception ex)
{
    WriteFatalError(ex, port);
    Environment.Exit(1);
}

// Prints a short, actionable summary for a startup/runtime crash instead of the .NET
// runtime's default "Unhandled exception." dump — several dozen lines of stack frames
// through Kestrel/LiteDB/Generic Host internals that bury the one line an operator
// actually needs. Recognizes the failure mode hit in practice (port already claimed by
// another TDA) and falls back to the exception's own message for anything else — still
// one line, not a stack trace, but honest about not having a canned tip. The full
// exception (with stack trace) is not lost — catching it here for the friendly summary
// means the runtime never prints its own dump, so this writes the full detail to a
// crash log instead, the same "friendly summary + details on disk" split PandoMail's
// FileLoggerProvider uses.
static void WriteFatalError(Exception ex, int port)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("────────────────────────────────────────────────────────────────────────────────");
    if (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"  TDA failed to start: port {port} is already in use.");
        Console.Error.WriteLine($"  Is another TDA already running on --port {port}? Stop it first, or pick a different port.");
    }
    else
    {
        Console.Error.WriteLine($"  TDA failed to start: {ex.Message}");
    }

    var crashLogPath = Path.Combine(AppContext.BaseDirectory, port.ToString(), "crash.log");
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath)!);
        File.AppendAllText(crashLogPath,
            $"{DateTimeOffset.UtcNow:O} {ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        Console.Error.WriteLine($"  Full details written to: {crashLogPath}");
    }
    catch
    {
        // Best-effort — if the crash log itself can't be written (e.g. the disk problem
        // that caused the crash), fall back to printing the full exception directly so
        // the operator isn't left with only the one-line summary above.
        Console.Error.WriteLine(ex.ToString());
    }
    Console.Error.WriteLine("────────────────────────────────────────────────────────────────────────────────");
    Console.Error.WriteLine();
}

// Deletes a file for --reset, tolerating a brief IOException instead of crashing the
// whole process with it. A prior TDA on this port releases its LiteDB file handles as
// part of the Generic Host's graceful shutdown (UseConsoleLifetime disposes the
// ServiceProvider, which disposes every registered LiteDB context) — but that release
// isn't guaranteed to have landed by the time a --reset immediately after it runs, and
// the same transient lock can come from a virus scanner or indexer touching the file.
// Retries with a short fixed delay rather than failing --reset's very first startup step.
static void DeleteWithRetry(string path, int maxAttempts = 20, int delayMs = 250)
{
    for (int attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try { File.Delete(path); return; }
        catch (IOException ex)
        {
            if (attempt == maxAttempts)
                // Surface a message that points at the actual cause instead of the .NET
                // runtime's generic "being used by another process" wording, which gives
                // no next step.
                throw new IOException(
                    $"'{Path.GetFileName(path)}' is still locked after {maxAttempts * delayMs / 1000.0:0.#}s. " +
                    "A previous TDA on this port may not have fully shut down yet — wait a few seconds and " +
                    "run --reset again, or check Task Manager for a lingering dotnet.exe.", ex);
            Thread.Sleep(delayMs);
        }
    }
}

// Resolves a configured DB path against AppContext.BaseDirectory so that relative
// paths in appsettings.json work regardless of the process working directory.
// Also creates the parent directory so LiteDB never fails on a missing folder.
static string ResolvePath(string? configured, string defaultName, int port)
{
    var portDir = Path.Combine(AppContext.BaseDirectory, port.ToString(), "mem");
    var path = configured is null
        ? Path.Combine(portDir, defaultName)
        : Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(portDir, configured);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return path;
}
