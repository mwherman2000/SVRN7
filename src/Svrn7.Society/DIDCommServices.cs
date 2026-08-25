using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Svrn7.Core;
using Svrn7.Core.Interfaces;
using Svrn7.Core.Models;
using Svrn7.DIDComm;
using Svrn7.Federation;

namespace Svrn7.Society;

// ── DIDCommTransferHandler ────────────────────────────────────────────────────

/// <summary>
/// Handles all incoming DIDComm transfer protocol messages for this Society.
/// All transfers — same-Society and cross-Society — arrive here after DIDComm unpack.
///
/// Protocol URIs handled:
///   Svrn7.Society/0.8.0/transfer-request      — citizen-initiated transfer
///   Svrn7.Society/0.8.0/transfer-order        — cross-Society TransferOrderCredential from another Society
///   Svrn7.Society/0.8.0/transfer-order-receipt— settlement confirmation from receiving Society
/// </summary>
public sealed class DIDCommTransferHandler : IDIDCommTransferHandler
{
    private readonly ISvrn7Driver          _driver;
    private readonly IDIDCommService       _didComm;
    private readonly IVcService            _vcService;
    private readonly ICryptoService        _crypto;
    private readonly Svrn7SocietyOptions   _opts;
    private readonly ILogger<DIDCommTransferHandler> _log;

    private readonly IProcessedOrderStore _processedOrders;

    public DIDCommTransferHandler(
        ISvrn7Driver driver,
        IDIDCommService didComm,
        IVcService vcService,
        ICryptoService crypto,
        IProcessedOrderStore processedOrders,
        IOptions<Svrn7SocietyOptions> opts,
        ILogger<DIDCommTransferHandler> log)
    {
        _driver    = driver;
        _didComm   = didComm;
        _vcService = vcService;
        _crypto    = crypto;
        _processedOrders = processedOrders;
        _opts      = opts.Value;
        _log       = log;
    }

    // ── Incoming transfer request (citizen → Society handler) ─────────────────

    public async Task<string> HandleTransferRequestAsync(
        string packedMessage, CancellationToken ct = default)
    {
        var message = await _didComm.UnpackAsync(
            packedMessage, _opts.SocietyMessagingPrivateKeyEd25519, ct);

        var request = JsonSerializer.Deserialize<TransferRequest>(message.Body)!;

        _log.LogInformation("Handling transfer request from {Payer} to {Payee} ({Amount} grana)",
            request.PayerDid, request.PayeeDid, request.AmountGrana);

        var result = await _driver.TransferAsync(request, ct);

        // Pack receipt
        var receipt = _didComm.NewMessage()
            .Type(Svrn7Constants.Protocols.TransferReceipt)
            .From(_opts.SocietyDid)
            .Body(new { success = result.Success, error = result.ErrorMessage })
            .Build();

        return await _didComm.PackEncryptedAsync(receipt,
            _opts.FederationMessagingPublicKeyEd25519,
            _opts.SocietyMessagingPrivateKeyEd25519,
            DIDCommPackMode.SignThenEncrypt, ct);
    }

    // ── Incoming TransferOrder from another Society ───────────────────────────

    public async Task<string> HandleTransferOrderAsync(
        string packedMessage, CancellationToken ct = default)
    {
        var message = await _didComm.UnpackAsync(
            packedMessage, _opts.SocietyMessagingPrivateKeyEd25519, ct);

        var order = JsonSerializer.Deserialize<TransferOrderCredential>(message.Body)!;

        _log.LogInformation(
            "Handling TransferOrder {TransferId} from Society {Origin} for payee {Payee}",
            order.TransferId, order.OriginSocietyDid, order.PayeeDid);

        // Idempotency: if already processed, return stored receipt (durable via LiteDB)
        var existingReceipt = await _processedOrders.GetReceiptAsync(order.TransferId, ct);
        if (existingReceipt is not null)
        {
            _log.LogDebug("TransferOrder {TransferId} already processed — returning cached receipt",
                order.TransferId);
            return existingReceipt;
        }

        // Credit the payee
        var payeeWallet = await _driver.GetBalanceResultAsync(order.PayeeDid, ct);
        var creditUtxo  = new Utxo
        {
            Id          = _crypto.Blake3Hex(System.Text.Encoding.UTF8.GetBytes(
                              $"CREDIT:{order.TransferId}")),
            OwnerDid    = order.PayeeDid,
            AmountGrana = order.AmountGrana,
        };

        // Direct wallet credit (bypasses validator — order VC is the proof)
        await _driver.AppendToLogAsync("CrossSocietyTransferCredit",
            JsonSerializer.Serialize(new
            {
                transferId       = order.TransferId,
                payeeDid         = order.PayeeDid,
                originSocietyDid = order.OriginSocietyDid,
                amountGrana      = order.AmountGrana,
            }), ct);

        // Build and cache receipt
        var receiptVc = new TransferReceiptCredential
        {
            TransferId       = order.TransferId,
            PayeeDid         = order.PayeeDid,
            CreditedGrana    = order.AmountGrana,
            TargetSocietyDid = _opts.SocietyDid,
            CreditedAt       = DateTimeOffset.UtcNow,
        };

        var receiptMsg = _didComm.NewMessage()
            .Type(Svrn7Constants.Protocols.TransferOrderReceipt)
            .From(_opts.SocietyDid)
            .Body(receiptVc)
            .Build();

        var packed = await _didComm.PackEncryptedAsync(receiptMsg,
            _opts.FederationMessagingPublicKeyEd25519,
            _opts.SocietyMessagingPrivateKeyEd25519,
            DIDCommPackMode.SignThenEncrypt, ct);

        await _processedOrders.StoreReceiptAsync(order.TransferId, packed, ct);
        return packed;
    }

    // ── Incoming TransferReceipt (settlement confirmation) ────────────────────

    public async Task HandleTransferReceiptAsync(
        string packedMessage, CancellationToken ct = default)
    {
        var message = await _didComm.UnpackAsync(
            packedMessage, _opts.SocietyMessagingPrivateKeyEd25519, ct);

        var receipt = JsonSerializer.Deserialize<TransferReceiptCredential>(message.Body)!;

        _log.LogInformation(
            "TransferOrder {TransferId} settled: {Amount} grana credited to {Payee} by {Society}",
            receipt.TransferId, receipt.CreditedGrana, receipt.PayeeDid, receipt.TargetSocietyDid);

        await _driver.AppendToLogAsync("CrossSocietyTransferSettled",
            JsonSerializer.Serialize(receipt), ct);
    }
}

// ── DIDCommMessageProcessorService ───────────────────────────────────────────

/// <summary>
/// Background service for periodic Society-level maintenance sweeps — VC expiry and
/// Merkle log auto-signing. Delegates to ISvrn7Driver; touches no DIDComm transport state.
///
/// Each sweep:
///   1. VC expiry sweep (delegates to ISvrn7Driver.ExpireStaleVcsAsync).
///   2. Merkle log auto-sign (delegates to ISvrn7Driver.SignMerkleTreeHeadAsync).
///
/// This service does NOT read from IInboxStore. Per DSA 0.24, DIDCommMessageSwitchboard
/// is the sole inbox reader — it already routes TransferRequest/TransferOrder/
/// TransferOrderReceipt to the Svrn7.Society LOBE (see Svrn7.Society.0.8.0.lobe.json),
/// which is the same coverage this service's inbox-drain step used to duplicate. A second
/// consumer dequeuing from the same store raced the Switchboard for messages and could
/// dead-letter one it grabbed first but didn't recognize (only 3 hardcoded types were
/// handled — everything else was marked Failed with no retry). Removed entirely rather
/// than gated behind "no TDA host", since every dequeue should go through the Switchboard.
/// </summary>
public sealed class DIDCommMessageProcessorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInboxStore          _inbox;
    private readonly Svrn7SocietyOptions  _opts;
    private readonly ILogger<DIDCommMessageProcessorService> _log;

    public DIDCommMessageProcessorService(
        IServiceScopeFactory scopeFactory,
        IInboxStore          inbox,
        IOptions<Svrn7SocietyOptions> opts,
        ILogger<DIDCommMessageProcessorService> log)
    {
        _scopeFactory = scopeFactory;
        _inbox        = inbox;
        _opts         = opts.Value;
        _log          = log;
    }

    /// <summary>
    /// Enqueues an incoming packed DIDComm message into the durable inbox for the
    /// Switchboard to pick up. Write path only — does not conflict with "sole inbox
    /// reader" since it never dequeues.
    /// </summary>
    public Task EnqueueAsync(string messageType, string packedMessage,
        string? fromDid = null, string? wireId = null, string? thid = null, string? jweEnvelope = null, string? traceContext = null, CancellationToken ct = default)
        => _inbox.EnqueueAsync(messageType, packedMessage, fromDid, wireId, thid, jweEnvelope, traceContext, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DIDCommMessageProcessorService started.");

        using var timer = new PeriodicTimer(_opts.BackgroundSweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSweepAsync(stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var driver = scope.ServiceProvider.GetRequiredService<ISvrn7Driver>();

        // 1. VC expiry sweep
        try
        {
            var expired = await driver.ExpireStaleVcsAsync(ct);
            if (expired > 0)
                _log.LogInformation("VC expiry sweep: {Count} credentials expired.", expired);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "VC expiry sweep failed.");
        }

        // 2. Merkle auto-sign
        try
        {
            var head = await driver.SignMerkleTreeHeadAsync(ct);
            _log.LogDebug("Merkle tree head signed. Root: {Root}", head.RootHash);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Merkle auto-sign failed.");
        }
    }
}