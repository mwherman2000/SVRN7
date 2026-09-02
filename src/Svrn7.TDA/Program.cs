using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
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
using Svrn7.Trust.AgentWallet;

// ── Web 7.0 Trusted Digital Assistant (TDA) — Console App Entry Point ────────
//
// Runtime: .NET 8 console app using Generic Host + Kestrel HTTP/2 + mTLS.
// Single inbound surface: POST /didcomm (KestrelListenerService).
//
// Per-identity runtime storage (docs/AGENTWALLET.md):
//   • Data root:  --data-root  ›  $PANDO_HOME  ›  ~/.web7-pando
//   • One directory per identity:  <data-root>/<name>-<genesisHash[..8]>/
//       ├── agent-identity.wallet   encrypted (Argon2id + AES-256-GCM) — keys + DB master key
//       ├── identity.meta.json      cleartext locator mirror (did, role, endpoint)
//       ├── lobes/                  per-instance LOBE set
//       └── mem/                    svrn7-*.db
//   • Wallet password:  $PANDO_WALLET_PASSWORD  ›  interactive prompt (double-entry on first run)
//   • Listen port:      first run auto-selects from --port-base and records it in the DID
//                       Document; every later run binds that exact port.

if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
{
    Console.WriteLine("""
        SVRN7 Trusted Digital Assistant (TDA)
        Web 7.0 Foundation — https://svrn7.net

        Usage:
          Svrn7.TDA --name <string> [--port <n>] [--port-base <n>] [--port-span <n>]
                    [--did <did>] [--url <url>] [--data-root <path>]
                    [--recovery-phrase "<12 words>"] [--federationdomain <domain>]
                    [--reset] [--jaeger [--jaeger-endpoint <url>]] [--help]

        Parameters:
          --name <string>   (required) Human-readable name for this TDA instance.
                            Selects the runtime directory and is stored in the DID
                            Document on first run.

          --port <n>        Listen port. On a first run it is used verbatim (no
                            auto-selection). On a later run it must match this
                            identity's published port or startup is refused.
                            Omit it to auto-select on first run and reuse the
                            published port thereafter.

          --port-base <n>   First candidate port for first-run auto-selection.
                            Default 8440.

          --port-span <n>   How many consecutive ports auto-selection may try.
                            Default 64.

          --did <did>       Select the identity by DID instead of by --name
                            (--name is still required for a first-run bootstrap).

          --url <url>       Base URL (scheme + host) advertised in the DID Document
                            service endpoint. Default: http://localhost
                            Full endpoint: <url>:<port>/didcomm

          --data-root <path>
                            Root directory for all per-identity data. Overrides
                            $PANDO_HOME. Default: ~/.web7-pando

          --recovery-phrase "<12 words>"
                            First run only: restore the identity from an existing
                            12-word BIP39 phrase instead of generating a new one.

          --federationdomain <domain>
                            Bare domain to auto-discover the Federation TDA endpoint
                            via drn.directory DNS at startup. Example: "svrn7.net".

          --reset           Delete this identity's entire runtime directory
                            (wallet, meta, lobes, databases) and re-bootstrap.
                            Irreversible. Prompts for confirmation on a terminal.

          --jaeger          Export DIDComm pipeline traces to Jaeger via OTLP/gRPC
                            instead of the console exporter.

          --jaeger-endpoint <url>
                            OTLP/gRPC endpoint to use with --jaeger. Implies --jaeger.

          --help | -h       Display this help and exit.

        Environment:
          PANDO_WALLET_PASSWORD   Wallet password. When unset, the TDA prompts on a
                                  terminal (and fails fast if there is none).
          PANDO_HOME              Data root (overridden by --data-root).
        """);
    Environment.Exit(0);
}

// ── Subcommands ─────────────────────────────────────────────────────────────

if (args.Length > 0 && args[0] == "db-shell")
    Environment.Exit(DbShell.Run(args[1..]));

// ── Argument parsing ─────────────────────────────────────────────────────────

string tdaName = RequireArg("--name");
string? didArg = OptionalArg("--did");
int?    portArg = OptionalArg("--port") is { } ps && int.TryParse(ps, out var pv) ? pv : null;
int     portBase = OptionalArg("--port-base") is { } pbs && int.TryParse(pbs, out var pbv) ? pbv : 8440;
int     portSpan = OptionalArg("--port-span") is { } pss && int.TryParse(pss, out var psv) ? psv : 64;
string  tdaUrl = (OptionalArg("--url") ?? "http://localhost").TrimEnd('/');
string? dataRootArg = OptionalArg("--data-root");
string? recoveryPhraseArg = OptionalArg("--recovery-phrase");
string  federationDomainArg = OptionalArg("--federationdomain")?.Trim() ?? string.Empty;
bool    forceReset = Array.IndexOf(args, "--reset") >= 0;

string? jaegerEndpointArg = OptionalArg("--jaeger-endpoint")?.Trim();
bool    useJaeger = Array.IndexOf(args, "--jaeger") >= 0 || jaegerEndpointArg is not null;
var     jaegerEndpoint = jaegerEndpointArg ?? "http://localhost:4317";

// ── Pre-host: data root, identity, wallet, port claim ────────────────────────
// All of this must run before Host.CreateDefaultBuilder so ConfigureServices can
// close over the resolved paths and the claimed port.

string          dataRoot;
string          lobeLibraryDir;
string          instanceDir;
string          memDir;
string          walletPath;
string          metaPath;
string          lobesConfigPath;
string          agentDid;
string          svrn7Name;
Svrn7Role       role;
string          secpPubHex;
string          x25519PubHex;
byte[]          signingKey;
byte[]          keyAgreementKey;
byte[]          dbMasterKey;
string?         parentTdaDid;
string?         parentTdaEndpointUrl;
bool            isFirstRun;
int             listenPort;
string?         freshRecoveryPhrase = null;
ListenPortClaim? portClaim = null;
string?         crashDir = null;

try
{
    dataRoot = PandoPaths.ResolveDataRoot(dataRootArg);
    crashDir = dataRoot;
    Directory.CreateDirectory(dataRoot);

    lobeLibraryDir = PandoPaths.LobeLibraryDir(dataRoot);
    Directory.CreateDirectory(lobeLibraryDir); // exists but may be empty — filled only by Publish (§D16)

    // ── Locate ──────────────────────────────────────────────────────────────
    var instances = PandoPaths.EnumerateInstances(dataRoot).ToList();
    string?       foundDir  = null;
    IdentityMeta? foundMeta = null;
    if (didArg is not null)
    {
        var hit = instances.Where(x => string.Equals(x.Meta.Did, didArg, StringComparison.Ordinal)).ToList();
        if (hit.Count == 1) (foundDir, foundMeta) = hit[0];
    }
    else
    {
        var hit = instances.Where(x => string.Equals(x.Meta.Name, tdaName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count > 1)
            Die($"more than one instance is named '{tdaName}' under {dataRoot}. Disambiguate with --did.");
        if (hit.Count == 1) (foundDir, foundMeta) = hit[0];
    }

    if (forceReset && foundDir is not null)
    {
        if (!ConfirmReset(foundDir))
            Die("--reset cancelled.");
        DeleteDirWithRetry(foundDir);
        Console.WriteLine($"--reset: deleted {foundDir}");
        foundDir = null;
        foundMeta = null;
    }
    else if (forceReset)
    {
        Console.WriteLine($"--reset: no existing instance for '{didArg ?? tdaName}' — nothing to delete.");
    }

    isFirstRun = foundDir is null;

    // ── Wallet: create or unlock ────────────────────────────────────────────
    var (pinStore, pinWarn) = AgentWalletPinStore();
    if (pinWarn is not null)
        Console.Error.WriteLine($"WARNING: public-key pinning disabled — {pinWarn}");

    char[] password = WalletPasswordPrompt.Acquire(firstRunCreate: isFirstRun);
    try
    {
        if (isFirstRun)
        {
            var phrase = recoveryPhraseArg ?? RecoveryPhrase.Generate();
            string genesisHash;
            using (var probe = RecoveryPhrase.Derive(phrase))
                genesisHash = GenesisHash.Compute(probe.Secp256k1PublicKeyHex);

            instanceDir = PandoPaths.InstanceDir(dataRoot, tdaName, genesisHash);
            if (Directory.Exists(instanceDir))
                Die($"'{instanceDir}' exists but has no identity.meta.json (corrupt or partial). Remove it or run --reset.");

            Directory.CreateDirectory(PandoPaths.MemDir(instanceDir));
            Directory.CreateDirectory(PandoPaths.LobesDir(instanceDir));
            crashDir = instanceDir;

            walletPath = PandoPaths.WalletPath(instanceDir);
            metaPath = PandoPaths.MetaPath(instanceDir);

            var svc = new AgentWalletService(walletPath, pinStore);
            using var id = svc.Create(
                password,
                h => $"did:drn:wanderer.svrn7.net/agent/1.0/{h}",
                Svrn7Role.Wanderer.ToString(),
                recoveryPhrase: phrase);

            agentDid = id.Did;
            svrn7Name = tdaName;
            role = Svrn7Role.Wanderer;
            secpPubHex = id.Secp256k1PublicKeyHex;
            x25519PubHex = id.X25519PublicKeyHex;
            signingKey = id.Secp256k1PrivateKey.ToArray();
            keyAgreementKey = id.X25519PrivateKey.ToArray();
            dbMasterKey = id.DbMasterKey.ToArray();
            parentTdaDid = null;
            parentTdaEndpointUrl = null;

            if (recoveryPhraseArg is null)
                freshRecoveryPhrase = phrase;
        }
        else
        {
            instanceDir = foundDir!;
            crashDir = instanceDir;
            walletPath = PandoPaths.WalletPath(instanceDir);
            metaPath = PandoPaths.MetaPath(instanceDir);

            var svc = new AgentWalletService(walletPath, pinStore);
            var unlock = svc.Unlock(() => (char[])password.Clone());
            switch (unlock)
            {
                case AgentUnlockResult.Success ok:
                    using (ok.Identity)
                    {
                        agentDid = ok.Identity.Did;
                        svrn7Name = foundMeta!.Name;
                        role = Enum.TryParse<Svrn7Role>(ok.Identity.Role, out var r) ? r : Svrn7Role.Wanderer;
                        secpPubHex = ok.Identity.Secp256k1PublicKeyHex;
                        x25519PubHex = ok.Identity.X25519PublicKeyHex;
                        signingKey = ok.Identity.Secp256k1PrivateKey.ToArray();
                        keyAgreementKey = ok.Identity.X25519PrivateKey.ToArray();
                        dbMasterKey = ok.Identity.DbMasterKey.ToArray();
                        parentTdaDid = ok.Identity.ParentTdaDid;
                        parentTdaEndpointUrl = ok.Identity.ParentTdaEndpointUrl;
                    }
                    break;

                case AgentUnlockResult.WrongPassword:
                    Die("wrong wallet password.");
                    return;
                case AgentUnlockResult.Throttled t:
                    Die($"wallet is locked out for another {t.RetryAfter.TotalSeconds:0}s after repeated failures.");
                    return;
                case AgentUnlockResult.PinMismatch:
                    Die($"wallet public key does not match its pin — '{walletPath}' was replaced or rolled back.");
                    return;
                case AgentUnlockResult.NoWallet:
                    Die($"no wallet at '{walletPath}'. Run with --reset to re-bootstrap this identity.");
                    return;
                default:
                    Die($"unexpected unlock result: {unlock.GetType().Name}");
                    return;
            }
        }
    }
    finally
    {
        Array.Clear(password);
    }

    memDir = PandoPaths.MemDir(instanceDir);
    lobesConfigPath = Path.Combine(PandoPaths.LobesDir(instanceDir), "lobes.config.json");

    // ── Port claim (atomic — docs/AGENTWALLET.md §D11 approach C) ────────────
    int claimBase;
    bool allowAuto;
    if (isFirstRun)
    {
        claimBase = portArg ?? portBase;
        allowAuto = portArg is null; // an explicit --port on first run is used verbatim
    }
    else
    {
        var published = foundMeta!.EndpointPort();
        if (portArg is not null && published is not null && portArg != published)
            Die($"--port {portArg} conflicts with this identity's published port {published}. " +
                "Omit --port, or move the endpoint (deferred — see docs/AGENTWALLET.md §D12).");
        claimBase = published ?? portArg ?? portBase;
        allowAuto = false;
    }

    var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    portClaim = ListenPortClaim.Acquire(claimBase, portSpan, allowAuto, loggerFactory.CreateLogger("ListenPortClaim"));
    listenPort = portClaim.Port;
}
catch (Exception ex)
{
    portClaim?.Dispose();
    WriteFatalError(ex, crashDir);
    Environment.Exit(1);
    return;
}

var serviceEndpointUrl = $"{tdaUrl}:{listenPort}/didcomm";

try
{

var host = Host.CreateDefaultBuilder(args)
    .UseConsoleLifetime()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Debug);
        logging.AddSimpleConsole(opts =>
        {
            opts.TimestampFormat = "HH:mm:ss.fff ";
            opts.UseUtcTimestamp = true;
            opts.SingleLine      = false;
        });
    })
    .ConfigureServices((ctx, services) =>
    {
        // The atomically-claimed listen socket, handed to KestrelListenerService.
        services.AddSingleton(portClaim!);

        // ── 0. OpenTelemetry tracing ─────────────────────────────────────────
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName:       Svrn7Telemetry.SourceName,
                serviceVersion:    Svrn7Telemetry.SourceVersion,
                serviceInstanceId: $"{tdaName}:{listenPort}"))
            .WithTracing(tracing =>
            {
                tracing.SetSampler(new AlwaysOnSampler())
                       .AddSource(Svrn7Telemetry.SourceName)
                       .AddSource(DIDDocumentService.ActivitySource.Name);

                if (useJaeger)
                    tracing.AddOtlpExporter(o => o.Endpoint = new Uri(jaegerEndpoint));
                else
                    tracing.AddConsoleExporter();
            });

        // ── 1. SVRN7 Society stack ───────────────────────────────────────────
        services.AddSvrn7Society(opts =>
        {
            opts.SocietyDid                        = ctx.Configuration["Svrn7:SocietyDid"]   ?? string.Empty;
            opts.FederationDid                     = ctx.Configuration["Svrn7:FederationDid"] ?? string.Empty;
            opts.Svrn7DbPath                       = ResolveDbPath(ctx.Configuration["Svrn7:DbPath"],        "svrn7.db",        memDir);
            opts.DidsDbPath                        = ResolveDbPath(ctx.Configuration["Svrn7:DidsDbPath"],    "svrn7-dids.db",   memDir);
            opts.VcsDbPath                         = ResolveDbPath(ctx.Configuration["Svrn7:VcsDbPath"],     "svrn7-vcs.db",    memDir);
            opts.MsgDbPath                         = ResolveDbPath(ctx.Configuration["Svrn7:MsgDbPath"],     "svrn7-msg.db",    memDir);
            opts.SchemasDbPath                     = ResolveDbPath(ctx.Configuration["Svrn7:SchemasDbPath"], "svrn7-schemas.db", memDir);
            opts.SocietyMessagingPrivateKeyEd25519 = []; // supplied at runtime

            // Every svrn7-*.db for this instance is AES-encrypted at rest under the
            // DB master key from the unlocked wallet (docs/AGENTWALLET.md §D9).
            opts.DatabasePassword                 = Convert.ToHexString(dbMasterKey).ToLowerInvariant();
        });

        services.AddSvrn7SocietyBackgroundServices();

        // ── 2. TDA Host ──────────────────────────────────────────────────────
        services.AddSvrn7Tda(opts =>
        {
            opts.SocietyDid                        = ctx.Configuration["Tda:SocietyDid"] ?? string.Empty;
            opts.SocietyMessagingPrivateKeyEd25519 = []; // supplied at runtime
            opts.ListenPort                        = listenPort;
            opts.ListenPortBase                    = portBase;
            opts.ListenPortSpan                    = portSpan;
            opts.AllowPortAutoSelect               = false; // the claim already happened in the pre-host block
            opts.BaseUrl                           = tdaUrl;
            opts.Role                              = role;
            opts.TlsCertificatePath               = ctx.Configuration["Tda:TlsCertPath"];
            opts.TlsCertificatePassword           = ctx.Configuration["Tda:TlsCertPassword"];
            opts.RequireMutualTls                 = bool.Parse(ctx.Configuration["Tda:RequireMutualTls"] ?? "true");
            opts.AcceptSelfSignedPeerCertificates = bool.Parse(ctx.Configuration["Tda:AcceptSelfSigned"] ?? "false");
            opts.MinRunspaces                     = 2;
            opts.MaxRunspaces                     = 0;
            opts.LobesConfigPath                  = ctx.Configuration["Tda:LobesConfigPath"] ?? lobesConfigPath;
            opts.LobeLibraryDir                   = lobeLibraryDir;
            opts.IdentityMetaPath                 = metaPath;
            opts.InstanceDir                      = instanceDir;
            opts.DatabaseMasterKey               = dbMasterKey;
            opts.ParentTdaDid                     = ctx.Configuration["Tda:ParentTdaDid"]         ?? string.Empty;
            opts.ParentTdaEndpointUrl             = ctx.Configuration["Tda:ParentTdaEndpointUrl"] ?? string.Empty;
            opts.FederationDomain                 = !string.IsNullOrEmpty(federationDomainArg)
                                                    ? federationDomainArg
                                                    : ctx.Configuration["Tda:FederationDomain"] ?? string.Empty;
        });
    })
    .Build();

host.Services.GetRequiredService<TracerProvider>();

var driver  = host.Services.GetRequiredService<ISvrn7SocietyDriver>();
var tdaOpts = host.Services.GetRequiredService<IOptions<TdaOptions>>().Value;

// ── DID Document + identity.meta.json ────────────────────────────────────────
if (isFirstRun)
{
    if (await driver.DidRegistry.CountAsync() == 0)
    {
        var didDoc = driver.CreateDidDocument(agentDid, secpPubHex, "drn",
                         serviceEndpointUrl, role, svrn7Name,
                         x25519PublicKeyHex: x25519PubHex);
        await driver.CreateDidAsync(didDoc);
    }
    tdaOpts.Role = role;

    new IdentityMeta
    {
        Did                   = agentDid,
        Name                  = svrn7Name,
        Role                  = role.ToString(),
        Secp256k1PublicKeyHex = secpPubHex,
        ServiceEndpointUrl    = serviceEndpointUrl,
        CreatedUtc            = DateTimeOffset.UtcNow.ToString("O"),
    }.Save(metaPath);
}
else
{
    var result = await driver.DidRegistry.ResolveAsync(agentDid);
    tdaOpts.Role = result.Document?.Role ?? role;
    svrn7Name    = result.Document?.Svrn7Name ?? svrn7Name;
    role         = tdaOpts.Role;

    // Keep the cleartext mirror honest with what this run is actually serving.
    var meta = IdentityMeta.TryLoad(metaPath) ?? new IdentityMeta { Did = agentDid, Name = svrn7Name };
    if (meta.ServiceEndpointUrl != serviceEndpointUrl || meta.Role != role.ToString())
    {
        meta.Did = agentDid;
        meta.Name = svrn7Name;
        meta.Role = role.ToString();
        meta.Secp256k1PublicKeyHex = secpPubHex;
        meta.ServiceEndpointUrl = serviceEndpointUrl;
        if (string.IsNullOrEmpty(meta.CreatedUtc)) meta.CreatedUtc = DateTimeOffset.UtcNow.ToString("O");
        meta.Save(metaPath);
    }
}

tdaOpts.AgentSigningPrivateKey      = signingKey;
tdaOpts.AgentKeyAgreementPrivateKey = keyAgreementKey;
tdaOpts.LocalDid                    = agentDid;
tdaOpts.ServiceEndpointUrl          = serviceEndpointUrl;
tdaOpts.AgentIdentityPath           = walletPath;

if (!string.IsNullOrEmpty(parentTdaDid) && string.IsNullOrEmpty(tdaOpts.ParentTdaDid))
    tdaOpts.ParentTdaDid = parentTdaDid;
if (!string.IsNullOrEmpty(parentTdaEndpointUrl) && string.IsNullOrEmpty(tdaOpts.ParentTdaEndpointUrl))
    tdaOpts.ParentTdaEndpointUrl = parentTdaEndpointUrl;

// ── drn.directory Federation endpoint discovery ─────────────────────────────
if (!string.IsNullOrEmpty(tdaOpts.FederationDomain) && string.IsNullOrEmpty(tdaOpts.FederationEndpointUrl))
{
    var discovered = await DrnDirectory.GetFederationEndpointAsync(tdaOpts.FederationDomain);
    if (discovered is not null)
        tdaOpts.FederationEndpointUrl = discovered;
}

// ── Startup banner ──────────────────────────────────────────────────────────
{
    var rawVersion = typeof(Program).Assembly
                         .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                         ?.InformationalVersion
                     ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
                     ?? "0.0.0";
    var version = rawVersion.Contains('+') ? rawVersion[..rawVersion.IndexOf('+')] : rawVersion;

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
    Console.WriteLine($"  TDA Name    : {svrn7Name}");
    Console.WriteLine($"  TDA Role    : {tdaOpts.Role}");
    Console.WriteLine($"  Bootstrap   : {(isFirstRun ? "first run — new identity created" : "existing identity unlocked")}");
    Console.WriteLine($"  Agent DID   : {agentDid}");
    Console.WriteLine($"  Data root   : {dataRoot}");
    Console.WriteLine($"  Instance    : {instanceDir}");
    Console.WriteLine($"  Endpoint    : {serviceEndpointUrl}{(isFirstRun && portArg is null && portClaim!.Port != portBase ? "  (auto-selected)" : "")}");
    Console.WriteLine($"  Fed Domain  : {(!string.IsNullOrEmpty(tdaOpts.FederationDomain)    ? tdaOpts.FederationDomain    : "(not configured)")}");
    Console.WriteLine($"  Fed Endpoint: {(!string.IsNullOrEmpty(tdaOpts.FederationEndpointUrl) ? tdaOpts.FederationEndpointUrl : "(not resolved)")}");
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
        Console.WriteLine($"  Federation  : (not yet initialised)");
    }
    Console.WriteLine(hr);
    if (freshRecoveryPhrase is not null)
    {
        Console.WriteLine("  RECOVERY PHRASE — write this down now, it is shown only once:");
        Console.WriteLine($"    {freshRecoveryPhrase}");
        Console.WriteLine(hr);
    }
    Console.WriteLine();
}

await host.RunAsync();

}
catch (Exception ex)
{
    portClaim?.Dispose();
    WriteFatalError(ex, crashDir);
    Environment.Exit(1);
}

// ── Argument helpers ────────────────────────────────────────────────────────

string RequireArg(string flag)
{
    var v = OptionalArg(flag);
    if (string.IsNullOrWhiteSpace(v))
    {
        Console.Error.WriteLine($"ERROR: {flag} <value> is required.");
        Environment.Exit(1);
    }
    return v!;
}

string? OptionalArg(string flag)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[i + 1] : null;
}

[DoesNotReturn]
static void Die(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    Environment.Exit(1);
}

static (Svrn7.Trust.AgentWallet.IPinStore Store, string? Warning) AgentWalletPinStore()
{
    var r = Svrn7.Trust.AgentWallet.PinStores.CreateDefault();
    return (r.Store, r.UnavailableReason);
}

static bool ConfirmReset(string dir)
{
    if (Console.IsInputRedirected) return true; // non-interactive (testnet scripts) — proceed
    Console.Write($"--reset will permanently delete '{dir}' and everything in it. Continue? [y/N] ");
    var answer = Console.ReadLine()?.Trim();
    return answer is "y" or "Y" or "yes" or "YES";
}

// Prints a short, actionable summary for a startup crash instead of the runtime's
// default unhandled-exception dump, and writes the full exception to a crash log.
static void WriteFatalError(Exception ex, string? crashDir)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("────────────────────────────────────────────────────────────────────────────────");
    if (ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.GetType().Name.Contains("AddressInUse", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("  TDA failed to start: the listen port is already in use.");
        Console.Error.WriteLine("  Stop whatever holds it, widen --port-span, or omit --port to auto-select.");
    }
    else
    {
        Console.Error.WriteLine($"  TDA failed to start: {ex.Message}");
    }

    var crashLogPath = Path.Combine(crashDir ?? AppContext.BaseDirectory, "crash.log");
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(crashLogPath))!);
        File.AppendAllText(crashLogPath,
            $"{DateTimeOffset.UtcNow:O} {ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        Console.Error.WriteLine($"  Full details written to: {crashLogPath}");
    }
    catch
    {
        Console.Error.WriteLine(ex.ToString());
    }
    Console.Error.WriteLine("────────────────────────────────────────────────────────────────────────────────");
    Console.Error.WriteLine();
}

// Deletes an instance directory for --reset, tolerating a brief IOException from a
// prior TDA's LiteDB handles not yet released (or a scanner/indexer touching a file).
static void DeleteDirWithRetry(string dir, int maxAttempts = 20, int delayMs = 250)
{
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try { Directory.Delete(dir, recursive: true); return; }
        catch (IOException) when (attempt < maxAttempts) { Thread.Sleep(delayMs); }
        catch (UnauthorizedAccessException) when (attempt < maxAttempts) { Thread.Sleep(delayMs); }
    }
    throw new IOException(
        $"'{dir}' is still locked after {maxAttempts * delayMs / 1000.0:0.#}s. " +
        "A previous TDA for this identity may not have fully shut down — wait a few seconds and retry, " +
        "or check for a lingering dotnet process.");
}

// Resolves a configured DB path: rooted paths used as-is, relative names placed
// under the instance's mem/ directory. Creates the parent so LiteDB never fails
// on a missing folder.
static string ResolveDbPath(string? configured, string defaultName, string memDir)
{
    var path = configured is null
        ? Path.Combine(memDir, defaultName)
        : Path.IsPathRooted(configured) ? configured : Path.Combine(memDir, configured);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    return path;
}

/// <summary>Marker type so <c>typeof(Program)</c> works for assembly-version reflection in the banner.</summary>
internal sealed partial class Program;
