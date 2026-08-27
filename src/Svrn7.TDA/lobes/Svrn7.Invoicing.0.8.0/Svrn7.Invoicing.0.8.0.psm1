#Requires -Version 7.0
<#
.SYNOPSIS
    SVRN7 Invoicing LOBE — invoice processing via DIDComm invoice protocol.

.DESCRIPTION
    Implements the did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/* DIDComm protocol.
    Computes SVRN7 transfer amounts from invoice line items and issues a
    TransferReceiptCredential VC as a DIDComm Svrn7.Invoicing/0.8.0/receipt.

    Derived from: Agent N — Invoicing (PowerShell Runspace) — DSA 0.24 Epoch 0 (PPML).

.NOTES
    DEFERRED — auto-transfer-on-invoice is not implemented. ConvertFrom-Web7InvoiceRequest
    is registered as the inbound entrypoint and only extracts/validates fields today; it
    does not call a transfer function and does not itself trigger outbound delivery
    (returns a hashtable, not an OutboundMessage — see LOBEGUIDE.md Appendix D). The
    pipeline below is the intended design, not live behavior.

    Do NOT complete this by having a registered entrypoint call
    Invoke-Svrn7Transfer / Invoke-Svrn7ExternalTransfer (admin-tools/Svrn7.AdminTools.psm1)
    directly — those cmdlets require a raw payer private key as a parameter, and this file
    is an eager LOBE: anything it references is loaded into the Switchboard's shared
    InitialSessionState, reachable by any dispatch runspace. Wiring a payer key into a
    message-triggered call path here is exactly what admin-tools/Svrn7.AdminTools.psm1 was
    split out to prevent (see LOBEGUIDE.md "Division of Responsibility").

    The correct completion follows Invoke-Svrn7IncomingTransfer's existing pattern
    (Svrn7.Society.0.8.0.psm1): add a C# driver method that signs and transfers using this
    TDA's own already-loaded key material (the same way HandleIncomingTransferMessageAsync
    and SignMerkleTreeHeadAsync keep their key internal) instead of accepting one from the
    caller. Until that exists, this LOBE only extracts/validates invoice fields.

    Protocol URIs:
        did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/request — inbound invoice request
        did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/receipt — outbound transfer receipt

    Intended pipeline (not yet wired to auto-execute — see DEFERRED note above):
        Dequeue-Svrn7Message | ConvertFrom-Web7InvoiceRequest |
        Resolve-InvoiceAmount | Invoke-Svrn7Transfer |
        New-Web7InvoiceReceipt | Enqueue-Svrn7Message
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── ConvertFrom-Web7InvoiceRequest ─────────────────────────────────────────────

function ConvertFrom-Web7InvoiceRequest {
    <#
    .SYNOPSIS
        Extracts invoice fields from an inbound Svrn7.Invoicing/0.8.0/request message.

    .PARAMETER MessageDid
        TDA resource DID URL of the inbox message.

    .OUTPUTS
        Hashtable — { MessageDid, PayerDid, PayeeDid, LineItems[], DueDate, Currency }

    .EXAMPLE
        Dequeue-Svrn7Message -Did $msgDid | ConvertFrom-Web7InvoiceRequest
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "Invoicing LOBE: message $MessageDid not found."
            return $null
        }

        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop

        Assert-BodyFields $body @('payerDid','payeeDid','lineItems') 'Invoicing LOBE: Svrn7.Invoicing/0.8.0/request'
        if ($body.lineItems.Count -eq 0) {
            throw "Invoicing LOBE: Svrn7.Invoicing/0.8.0/request has no lineItems."
        }

        return @{
            MessageDid  = $MessageDid
            PayerDid    = $body.payerDid
            PayeeDid    = $body.payeeDid
            LineItems   = $body.lineItems   # array of { description, amountGrana }
            DueDate     = $body.dueDate
            Currency    = $body.currency ?? 'SRC'
            InvoiceId   = $body.invoiceId ?? [guid]::NewGuid().ToString()
            RequestedAt = [datetimeoffset]::UtcNow.ToString('o')
        }
    }
}

# ── Resolve-InvoiceAmount ─────────────────────────────────────────────────────

function Resolve-InvoiceAmount {
    <#
    .SYNOPSIS
        Computes the total transfer amount in grana from invoice line items.

    .PARAMETER Invoice
        Invoice hashtable from ConvertFrom-Web7InvoiceRequest (pipeline input).

    .OUTPUTS
        Hashtable — invoice extended with TotalGrana field.

    .EXAMPLE
        ConvertFrom-Web7InvoiceRequest | Resolve-InvoiceAmount
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [hashtable] $Invoice
    )

    process {
        $total = 0L
        foreach ($item in $Invoice.LineItems) {
            if ($item.amountGrana -lt 0) {
                throw "Invoicing LOBE: line item '$($item.description)' has negative amountGrana."
            }
            $total += [long]$item.amountGrana
        }

        if ($total -le 0) {
            throw "Invoicing LOBE: invoice total must be greater than zero (got $total grana)."
        }

        $Invoice['TotalGrana'] = $total
        Write-Verbose "Invoicing LOBE: invoice $($Invoice.InvoiceId) total = $total grana"
        return $Invoice
    }
}

# ── New-Web7InvoiceReceipt ─────────────────────────────────────────────────────

function New-Web7InvoiceReceipt {
    <#
    .SYNOPSIS
        Builds an Svrn7.Invoicing/0.8.0/receipt OutboundMessage after a successful transfer.

    .DESCRIPTION
        Accepts the transfer result hashtable (pipeline input) and constructs
        a DIDComm receipt containing the TransferReceiptCredential VC.

    .PARAMETER TransferResult
        Result hashtable from Invoke-Svrn7Transfer or Invoke-Svrn7ExternalTransfer.
        Expected fields: TransferId, PayerDid, PayeeDid, AmountGrana, ReceiptVcId,
                         InvoiceId, Success.

    .OUTPUTS
        OutboundMessage — packed DIDComm message ready for Switchboard delivery.

    .EXAMPLE
        ConvertFrom-Web7InvoiceRequest | Resolve-InvoiceAmount |
            Invoke-Svrn7Transfer | New-Web7InvoiceReceipt | Enqueue-Svrn7Message
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [hashtable] $TransferResult
    )

    process {
        $endpoint = Resolve-SocietySenderEndpoint -Did $TransferResult.PayerDid
        if (-not $endpoint) {
            Write-Warning "New-Web7InvoiceReceipt: no DIDComm service endpoint for '$($TransferResult.PayerDid)' — reply skipped."
            return
        }

        Write-Verbose "Invoicing LOBE: receipt for invoice $($TransferResult.InvoiceId) — transferId $($TransferResult.TransferId)"

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            type = 'did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/receipt'
            from = $SVRN7.LocalDid
            to   = @($TransferResult.PayerDid)
            body = [ordered]@{
                from           = $SVRN7.LocalDid
                to             = $TransferResult.PayerDid
                success        = $TransferResult.Success
                invoiceId      = $TransferResult.InvoiceId
                transferId     = $TransferResult.TransferId
                payerDid       = $TransferResult.PayerDid
                payeeDid       = $TransferResult.PayeeDid
                amountGrana    = $TransferResult.AmountGrana
                receiptVcId    = $TransferResult.ReceiptVcId
                settledAt      = [datetimeoffset]::UtcNow.ToString('o')
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new($endpoint, $envelope)
    }
}

# ── Send-Web7InvoiceError ──────────────────────────────────────────────────────

function Send-Web7InvoiceError {
    <#
    .SYNOPSIS
        Sends an Svrn7.Invoicing/0.8.0/receipt with success=false on transfer failure.

    .PARAMETER PayerDid
        The requesting payer's DID.

    .PARAMETER InvoiceId
        The invoice identifier.

    .PARAMETER ErrorMessage
        Human-readable error description.

    .OUTPUTS
        OutboundMessage — packed DIDComm message ready for Switchboard delivery.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $PayerDid,
        [Parameter(Mandatory)] [string] $InvoiceId,
        [Parameter(Mandatory)] [string] $ErrorMessage
    )

    process {
        $endpoint = Resolve-SocietySenderEndpoint -Did $PayerDid
        if (-not $endpoint) {
            Write-Warning "Send-Web7InvoiceError: no DIDComm service endpoint for '$PayerDid' — reply skipped."
            return
        }

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            type = 'did:drn:svrn7.net/protocols/Svrn7.Invoicing.0.8.0/receipt'
            from = $SVRN7.LocalDid
            to   = @($PayerDid)
            body = [ordered]@{
                from       = $SVRN7.LocalDid
                to         = $PayerDid
                success    = $false
                invoiceId  = $InvoiceId
                error      = $ErrorMessage
                failedAt   = [datetimeoffset]::UtcNow.ToString('o')
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new($endpoint, $envelope)
    }
}

# ── Helpers ───────────────────────────────────────────────────────────────────

Export-ModuleMember -Function @(
    'ConvertFrom-Web7InvoiceRequest',
    'Resolve-InvoiceAmount',
    'New-Web7InvoiceReceipt',
    'Send-Web7InvoiceError'
)
