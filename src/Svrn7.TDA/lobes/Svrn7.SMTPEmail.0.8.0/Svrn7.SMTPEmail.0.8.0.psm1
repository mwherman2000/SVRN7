#Requires -Version 7.0
<#
.SYNOPSIS
    SVRN7 SMTPEmail LOBE — generic DIDComm-native email transport, usable by any
    DIDComm-capable email client (PandoMail or otherwise), not just one app.

.DESCRIPTION
    Implements the did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/* DIDComm
    protocol. RFC 5322 email messages are tunneled verbatim inside DIDComm
    envelopes and delivered TDA-to-TDA. "SMTPEmail" names the semantic role —
    RFC 5322 message interop, the same shape any SMTP-based client would
    recognize — not the literal SMTP wire protocol. No SMTP server, no MX
    records, no port 25: transport is DIDComm end to end.

    Derived from: Email LOBE (Agent 1 LOBE) — DSA 0.24 Epoch 0 (PPML).

.NOTES
    Protocol URIs:
        did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/Signal-Email    — inbound/outbound email
        did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/issue-receipt  — delivery confirmation

    Key:
        From/To headers in the RFC 5322 payload use did: URIs, not SMTP addresses.
        The sender's DID is verified from the DIDComm envelope — not the From header.
        No SMTP server, no MX records, no MIME multipart (Epoch 0).

    App-specific local-UI queries (List-Emails, Query-TdaDid, Get-EmailBody, etc.)
    live in the separate PandoMail.0.8.0 LOBE, which depends on this one for the
    actual send/receive functions (Enqueue-Email, Get-Rfc5322Header) and the shared
    folder-count notification helper.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Dequeue-Email ────────────────────────────────────────────────────────────

function Dequeue-Email {
    <#
    .SYNOPSIS
        Processes an inbound DIDComm email/1.0/message and stores it locally.

    .DESCRIPTION
        Accepts an inbox message DID URL, resolves the message payload via
        $SVRN7.GetMessageAsync(), extracts the RFC 5322 body, verifies the
        sender's DID against the DIDComm envelope, and persists the email
        record to the IInboxStore long-term memory.

        Derived from: Email LOBE (Agent 1 LOBE) — DSA 0.24 Epoch 0 (PPML).
        Protocol: did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/Signal-Email

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.
        Form: did:drn:{networkId}/inbox/msg/{objectId}

    .OUTPUTS
        EmailRecord — the stored email record, or $null if processing failed.

    .EXAMPLE
        Dequeue-Email -MessageDid "did:drn:societytest.svrn7.net/inbox/msg/5f43a2b1c8e9d7f012345678"

    .NOTES
        The From header in the RFC 5322 payload is treated as display metadata only.
        The authoritative sender identity is the DIDComm envelope's 'from' field.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        Write-Verbose "SMTPEmail LOBE: processing inbound email $MessageDid"

        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "SMTPEmail LOBE: message $MessageDid not found."
            return $null
        }

        # Parse the DIDComm body — expected: { from, rfc5322Body }
        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $rfc5322 = $body.rfc5322Body
        if (-not $rfc5322) {
            Write-Warning "SMTPEmail LOBE: message $MessageDid has no rfc5322Body field."
            return $null
        }

        # Build the email record
        $record = @{
            MessageDid   = $MessageDid
            MessageId    = $msg.Id
            SenderDid    = $body.from          # authoritative — from DIDComm envelope
            ReceivedAt   = [datetimeoffset]::UtcNow.ToString('o')
            Rfc5322Body  = $rfc5322
            Subject      = (Get-Rfc5322Header -Raw $rfc5322 -Header 'Subject')
            FromHeader   = (Get-Rfc5322Header -Raw $rfc5322 -Header 'From')
            ToHeader     = (Get-Rfc5322Header -Raw $rfc5322 -Header 'To')
        }

        Write-Verbose "SMTPEmail LOBE: stored email from $($record.SenderDid) — '$($record.Subject)'"

        # Push Email-Notify to any attached local UI via the WebSocket hub.
        # The Switchboard delivers any OutboundMessage whose PeerEndpoint starts
        # with "ws://" through WebSocketNotifyHub.PushAsync instead of HTTP/2 POST.
        $notifyEnvelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            type = 'did:drn:svrn7.net/protocols/Email-Notify.0.1.0/new-message'
            from = $SVRN7.LocalDid
            to   = @($SVRN7.LocalDid)
            body = [ordered]@{
                messageDid = $MessageDid
                senderDid  = $record.SenderDid
                subject    = $record.Subject
                receivedAt = $record.ReceivedAt
            }
        } | ConvertTo-Json -Compress -Depth 3

        # Output the record for any pipeline caller, then the notification OutboundMessage.
        $record
        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $notifyEnvelope)
        New-FolderCountsNotification
    }
}

# ── Enqueue-Email ────────────────────────────────────────────────────────────

function Enqueue-Email {
    <#
    .SYNOPSIS
        Sends an RFC 5322 email message to a recipient TDA via DIDComm.

    .DESCRIPTION
        Constructs a DIDComm email/1.0/message body containing a full RFC 5322
        message. Resolves the recipient's DID to their TDA endpoint and returns
        an OutboundMessage for the Switchboard to deliver.

        Protocol: did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/Signal-Email

    .PARAMETER RecipientDid
        The recipient citizen's did:drn DID. Semicolon-separated for multiple To recipients.

    .PARAMETER Subject
        Email subject line.

    .PARAMETER Body
        Plain text email body.

    .PARAMETER From
        Sender display string, e.g. '"Alice" <did:drn:...>'. Defaults to the local DID.

    .PARAMETER ToDisplay
        To display string(s), e.g. '"Bob" <did:drn:...>; "Alice" <did:drn:...>'. Defaults
        to a comma-joined list of the RecipientDid entries.

    .PARAMETER Cc
        Semicolon-separated list of additional recipient DIDs to deliver a copy to.

    .PARAMETER CcDisplay
        Cc display string(s), e.g. '"Carol" <did:drn:...>; "Dave" <did:drn:...>'.
        Defaults to a comma-joined list of the Cc DIDs when not provided.

    .OUTPUTS
        OutboundMessage — one per successfully resolved recipient (every To and every Cc),
        packed and ready for Switchboard delivery.

    .EXAMPLE
        Enqueue-Email -RecipientDid "did:drn:beta.svrn7.net/citizen/bob" -Subject "Hello" -Body "Hi Bob" -Cc "did:drn:beta.svrn7.net/citizen/carol;did:drn:beta.svrn7.net/citizen/dave"
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RecipientDid,
        [Parameter(Mandatory)] [string] $Subject,
        [Parameter(Mandatory)] [string] $Body,
        [string] $From      = '',
        [string] $ToDisplay = '',
        [string] $Cc        = '',
        [string] $CcDisplay = ''
    )

    process {
        if (-not $From) { $From = $SVRN7.LocalDid }

        # Semicolon separates multiple recipients within the To: field and within the
        # Cc: field alike (matches the PandoMail compose UI's To/Cc text boxes).
        $toDids = @($RecipientDid -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $ccDids = @($Cc           -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

        $toDisplayValue = if ($ToDisplay) { $ToDisplay } else { $toDids -join ', ' }
        $ccDisplayValue = if ($CcDisplay) { $CcDisplay } else { $ccDids -join ', ' }

        $date = [datetime]::UtcNow.ToString('ddd, dd MMM yyyy HH:mm:ss') + ' +0000'

        # Build RFC 5322 headers — Cc: line only present when there are Cc recipients.
        $headerLines = [System.Collections.Generic.List[string]]::new()
        $headerLines.Add("From: $From")
        $headerLines.Add("To: $toDisplayValue")
        if ($ccDids.Count -gt 0) { $headerLines.Add("Cc: $ccDisplayValue") }
        $headerLines.Add("Subject: $Subject")
        $headerLines.Add("Date: $date")
        $headerLines.Add("MIME-Version: 1.0")
        $headerLines.Add("Content-Type: text/plain; charset=utf-8")
        $rfc5322 = ($headerLines -join "`r`n") + "`r`n`r`n$Body"

        # Deliver independently to every To recipient and every Cc recipient — one
        # physical DIDComm message per peer TDA, all carrying the same RFC 5322 body so
        # every recipient sees the full To/Cc header set. A failure resolving one
        # recipient's endpoint dead-letters only that copy; it does not block delivery
        # to the others (matches SMTP semantics: each envelope recipient is independent).
        $targets = $toDids + $ccDids

        foreach ($targetDid in $targets) {
            $targetEnvelope = [ordered]@{
                typ  = 'application/didcomm-plain+json'
                id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
                type = 'did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/Signal-Email'
                from = $SVRN7.LocalDid
                to   = @($targetDid)
                body = [ordered]@{
                    from        = $SVRN7.LocalDid
                    to          = $toDids
                    cc          = $ccDids
                    rfc5322Body = $rfc5322
                }
            } | ConvertTo-Json -Compress -Depth 3

            $peerEndpoint = Resolve-SocietySenderEndpoint -Did $targetDid
            if (-not $peerEndpoint) {
                Write-Warning "Enqueue-Email: no DIDComm service endpoint for '$targetDid' — writing to dead letters."
                $SVRN7.EnqueueDeadLetterAsync(
                    $targetDid,
                    $targetEnvelope,
                    'did:drn:svrn7.net/protocols/Svrn7.SMTPEmail.0.8.0/Signal-Email',
                    "No DIDComm service endpoint found for recipient '$targetDid'"
                ).GetAwaiter().GetResult()
                continue
            }

            [Svrn7.TDA.OutboundMessage]::new($peerEndpoint, $targetEnvelope)
        }

        New-FolderCountsNotification
    }
}

# ── New-FolderCountsNotification ─────────────────────────────────────────────
# Shared helper — queries current folder counts and returns an OutboundMessage
# that pushes Notify-FolderCounts over the local WebSocket hub. Called after
# every operation (here and in PandoMail.0.8.0) that changes inbox, sent, or
# dead-letter counts. Lives here (not in PandoMail.0.8.0) because it counts
# state owned by this generic transport LOBE; PandoMail.0.8.0 depends on this
# LOBE and calls this function directly.

function New-FolderCountsNotification {
    $counts = $SVRN7.CountEmailFoldersAsync().GetAwaiter().GetResult()
    $envelope = [ordered]@{
        typ  = 'application/didcomm-plain+json'
        id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
        type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Notify-FolderCounts'
        from = $SVRN7.LocalDid
        to   = @($SVRN7.LocalDid)
        body = [ordered]@{
            inboxCount      = $counts.Inbox
            sentCount       = $counts.Sent
            deadLetterCount = $counts.DeadLetters
        }
    } | ConvertTo-Json -Compress -Depth 3
    [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
}

# ── Helpers ───────────────────────────────────────────────────────────────────

function Get-Rfc5322Header {
    param([string] $Raw, [string] $Header)
    $pattern = "(?m)^${Header}:\s*(.+)$"
    if ($Raw -match $pattern) { return $Matches[1].Trim() }
    return $null
}

Export-ModuleMember -Function @(
    'Dequeue-Email',
    'Enqueue-Email',
    'New-FolderCountsNotification',
    'Get-Rfc5322Header'
)
