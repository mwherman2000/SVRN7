#Requires -Version 7.0
<#
.SYNOPSIS
    PandoMail LOBE — local-UI request handlers for the PandoMail app.

.DESCRIPTION
    Implements the did:drn:svrn7.net/protocols/PandoMail.0.8.0/* DIDComm
    protocol — the app-specific query/command surface that the PandoMail
    WinForms client (TdaMailClient) talks to over the local /localcomm-ws
    WebSocket. This LOBE does not itself implement email transport: sending
    and receiving RFC 5322 email between TDAs is the Svrn7.SMTPEmail.0.8.0
    LOBE's job, which this LOBE depends on and calls into directly
    (Enqueue-Email, Get-Rfc5322Header, New-FolderCountsNotification).

    Derived from: Email LOBE (Agent 1 LOBE) — DSA 0.24 Epoch 0 (PPML).

.NOTES
    Depends on Svrn7.SMTPEmail.0.8.0 being loaded in the same runspace —
    reliable today because that LOBE is eager-loaded by default (see
    lobes.config.json), not because of the declarative "dependencies.lobes"
    resolution in LobeManager (which only resolves flat paths under
    LobeBaseDir, not the per-LOBE subfolder layout used everywhere).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Invoke-PandoMailList ────────────────────────────────────────────────────

function Invoke-PandoMailList {
    <#
    .SYNOPSIS
        Handles a List-Emails query and replies with an Get-PandoMails response.

    .DESCRIPTION
        Queries the local inbox for processed email messages (newest-first, default
        limit 50) and delivers a Get-PandoMails DIDComm message to the sender's
        DID Document endpoint.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-Emails
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoMails

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering Get-PandoMails to the sender's endpoint,
        or $null if the sender's endpoint cannot be resolved.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: List-Emails message $MessageDid not found."
            return $null
        }

        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop

        $limit = 50
        if ($body.PSObject.Properties['limit']) { $limit = [int]$body.limit }

        $emails = $SVRN7.ListEmailsAsync($limit).GetAwaiter().GetResult()

        $emailList = @(foreach ($e in $emails) {
            $eBody = $e.PackedPayload | ConvertFrom-Json -ErrorAction SilentlyContinue
            $rfc5322 = Get-BodyField $eBody 'rfc5322Body' ''
            if (-not $rfc5322) { continue }
            [ordered]@{
                messageDid = $e.Id
                senderDid  = $e.FromDid
                subject    = (Get-Rfc5322Header -Raw $rfc5322 -Header 'Subject')
                fromHeader = (Get-Rfc5322Header -Raw $rfc5322 -Header 'From')
                toHeader   = (Get-Rfc5322Header -Raw $rfc5322 -Header 'To')
                ccHeader   = (Get-Rfc5322Header -Raw $rfc5322 -Header 'Cc')
                receivedAt = $e.ReceivedAt.ToString('o')
            }
        })

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoMails'
            from = $SVRN7.LocalDid
            to   = @($msg.FromDid)
            body = [ordered]@{
                emails = $emailList
                count  = $emailList.Count
            }
        } | ConvertTo-Json -Compress -Depth 5

        Write-Verbose "PandoMail LOBE: List-Emails returning $($emailList.Count) messages via WebSocket."
        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

# ── Invoke-PandoMailSend ─────────────────────────────────────────────────────

function Invoke-PandoMailSend {
    <#
    .SYNOPSIS
        Handles a Enqueue-PandoMail request from TdaMailClient and delivers to the recipient TDA.

    .DESCRIPTION
        Accepts a DIDComm message from local PandoMail UI. Body: { recipientDid, subject, bodyText,
        senderDisplay, recipientDisplay, cc, ccDisplay }. recipientDid and cc are semicolon-separated
        when there are multiple recipients. Builds an RFC 5322 message via Enqueue-Email (the
        Svrn7.SMTPEmail.0.8.0 LOBE) and returns an OutboundMessage per recipient for delivery.

        Protocol (inbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Enqueue-PandoMail

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] for the Switchboard to deliver, or $null on validation failure.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: Enqueue-PandoMail message $MessageDid not found."
            return $null
        }

        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop

        $recipientDid = Get-BodyField $body 'recipientDid'
        if (-not $recipientDid) {
            Write-Warning "PandoMail LOBE: Enqueue-PandoMail $MessageDid missing recipientDid — skipped."
            return $null
        }

        $subject          = Get-BodyField $body 'subject'          ''
        $bodyText         = Get-BodyField $body 'bodyText'         ''
        $senderDisplay    = Get-BodyField $body 'senderDisplay'    ''
        $recipientDisplay = Get-BodyField $body 'recipientDisplay' ''
        $cc               = Get-BodyField $body 'cc'               ''
        $ccDisplay        = Get-BodyField $body 'ccDisplay'        ''

        Write-Verbose "PandoMail LOBE: Enqueue-PandoMail — forwarding to $recipientDid ('$subject')"
        Enqueue-Email -RecipientDid $recipientDid -Subject $subject -Body $bodyText `
            -From $senderDisplay -ToDisplay $recipientDisplay -Cc $cc -CcDisplay $ccDisplay
    }
}

# ── Get-TdaDid ────────────────────────────────────────────────────────────────

function Get-TdaDid {
    <#
    .SYNOPSIS
        Returns this TDA's own DID to a requesting local UI client.

    .DESCRIPTION
        Handles a Query-TdaDid request from TdaMailClient. Replies with the
        TDA's LocalDid over the WebSocket push channel.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/Query-TdaDid
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Reply-TdaDid

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering Reply-TdaDid to the sender's endpoint,
        or $null if the sender's endpoint cannot be resolved.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) { return $null }

        $localName = ''
        try {
            $docJson = $SVRN7.GetDidDocumentJson($SVRN7.LocalDid)
            if ($docJson) {
                $doc = $docJson | ConvertFrom-Json -ErrorAction SilentlyContinue
                if ($null -ne $doc -and $doc.PSObject.Properties['Svrn7Name'] -and $doc.Svrn7Name) {
                    $localName = $doc.Svrn7Name
                }
            }
        } catch { }

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Reply-TdaDid'
            from = $SVRN7.LocalDid
            to   = @($SVRN7.LocalDid)
            body = [ordered]@{
                did  = $SVRN7.LocalDid
                name = $localName
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

# ── Invoke-Svrn7EmailGetEmailBody ─────────────────────────────────────────────

function Invoke-Svrn7EmailGetEmailBody {
    <#
    .SYNOPSIS
        Returns the full RFC 5322 body of a specific stored email message.

    .DESCRIPTION
        Handles a Get-EmailBody request from TdaMailClient. Looks up the target
        email in the inbox by its DID, extracts the RFC 5322 body and plain-text
        content, and replies via the WebSocket push channel.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-EmailBody
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Reply-EmailBody

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message containing the request.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering Reply-EmailBody to the WebSocket hub,
        or $null if the target message cannot be found.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: Get-EmailBody request message $MessageDid not found."
            return $null
        }

        $body      = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $targetDid = Get-BodyField $body 'messageDid' ''

        if (-not $targetDid) {
            Write-Warning "PandoMail LOBE: Get-EmailBody $MessageDid missing messageDid field."
            return $null
        }

        $emailMsg = $SVRN7.GetMessageAsync($targetDid).GetAwaiter().GetResult()

        if ($emailMsg) {
            # Inbox/Sent: PackedPayload is already the unpacked inner body — {rfc5322Body, ...}
            # for Inbox, {bodyText, ...} for Sent (handled below).
            $emailBody = $emailMsg.PackedPayload | ConvertFrom-Json -ErrorAction SilentlyContinue
            $rfc5322   = Get-BodyField $emailBody 'rfc5322Body' ''
        } else {
            # Not a live inbox/sent message — check the dead-letter store. Dead Letters rows
            # use the DeadLetterRecord's own Id as messageDid (a separate store from the
            # inbox), and PackedMessage is the FULL envelope (never unpacked, since it was
            # never actually sent or received) — one extra unwrap to reach the inner body
            # versus the inbox/sent PackedPayload case above.
            $deadLetter = @($SVRN7.ListDeadLettersAsync().GetAwaiter().GetResult()) |
                Where-Object { $_.Id -eq $targetDid } | Select-Object -First 1
            if (-not $deadLetter) {
                Write-Warning "PandoMail LOBE: Get-EmailBody target message $targetDid not found."
                return $null
            }
            $dlEnvelope = $deadLetter.PackedMessage | ConvertFrom-Json -ErrorAction SilentlyContinue
            $emailBody  = if ($dlEnvelope) { Get-BodyField $dlEnvelope 'body' $null } else { $null }
            $rfc5322    = if ($emailBody) { Get-BodyField $emailBody 'rfc5322Body' '' } else { '' }
        }

        # Extract plain-text body — everything after the first blank line in RFC 5322.
        # Inbox messages (Signal-PandoMail) carry rfc5322Body. Sent Items are stored as the
        # original Enqueue-PandoMail compose request instead — no RFC 5322 envelope was ever
        # persisted back onto that record (it's built per-recipient at delivery time and only
        # travels on the wire) — so fall back to the raw bodyText the compose UI submitted.
        $bodyText = ''
        if ($rfc5322) {
            $parts = $rfc5322 -split "`r?`n`r?`n", 2
            if ($parts.Count -ge 2) { $bodyText = $parts[1].Trim() }
        } else {
            $bodyText = Get-BodyField $emailBody 'bodyText' ''
        }

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Reply-EmailBody'
            from = $SVRN7.LocalDid
            to   = @($SVRN7.LocalDid)
            body = [ordered]@{
                messageDid  = $targetDid
                rfc5322Body = $rfc5322
                bodyText    = $bodyText
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

# ── Invoke-PandoMailResolveDid ────────────────────────────────────────────────

function Invoke-PandoMailResolveDid {
    <#
    .SYNOPSIS
        Resolves a DID Document on behalf of PandoMail and replies over WebSocket.

    .DESCRIPTION
        Handles a Resolve-PandoDid request from TdaMailClient.
        Tries the local DID registry first. On a local hit, pushes Reply-DidDocument
        immediately over the WebSocket hub (envelope thid = this request's WireId — the
        sender's own wire envelope id, which is what WebSocketNotifyHub.TrackCorrelation
        keyed on; $msg.Id is the TDA's internal storage DID and must never be used here).
        On a local miss, forwards a plaintext did-resolve-request to the parent TDA
        using this request's WireId as the Identity LOBE's requestId/originalRequestId
        body fields (that inter-TDA relay chain is unchanged — see Svrn7.Identity.0.8.0.psm1),
        so that Invoke-Svrn7DidResolveResponse can push the result back to WebSocket
        (again via thid) when the response arrives through the resolution chain.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/Resolve-PandoDid
        Protocol (outbound): did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/Reply-DidDocument (ws)
                             did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/did-resolve-request (http, on miss)

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: Resolve-PandoDid message $MessageDid not found."
            return $null
        }

        $body         = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $requestedDid = Get-BodyField $body 'requestedDid'  ''

        if (-not $requestedDid) {
            Write-Warning "PandoMail LOBE: Resolve-PandoDid $MessageDid missing requestedDid."
            return $null
        }

        # Try local registry first
        $didDoc = $SVRN7.Driver.ResolveDidAsync($requestedDid).GetAwaiter().GetResult()
        if ($null -ne $didDoc) {
            Write-Verbose "PandoMail LOBE: Resolve-PandoDid LOCAL HIT '$requestedDid'"
            # Use GetDidDocumentJson round-trip to read Svrn7Name — same pattern as Get-TdaDid.
            # Direct C# property access may return null if the field was absent when stored.
            $svrn7Name = ''
            try {
                $docJson = $SVRN7.GetDidDocumentJson($requestedDid)
                if ($docJson) {
                    $doc = $docJson | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($null -ne $doc -and $doc.PSObject.Properties['Svrn7Name'] -and $doc.Svrn7Name) {
                        $svrn7Name = $doc.Svrn7Name
                    }
                }
            } catch { }
            Write-Verbose "PandoMail LOBE: Resolve-PandoDid svrn7Name='$svrn7Name'"
            $replyEnvelope = [ordered]@{
                typ  = 'application/didcomm-plain+json'
                id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
                thid = $msg.WireId
                type = 'did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/Reply-DidDocument'
                from = $SVRN7.LocalDid
                to   = @($SVRN7.LocalDid)
                body = [ordered]@{
                    requestedDid = $requestedDid
                    found        = $true
                    svrn7Name    = $svrn7Name
                }
            } | ConvertTo-Json -Compress -Depth 3
            return [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $replyEnvelope)
        }

        # Local miss — escalate to parent TDA if available
        $parentEndpoint = $SVRN7.ParentTdaEndpointUrl
        $parentDid      = $SVRN7.ParentTdaDid

        if (-not $parentEndpoint) {
            Write-Verbose "PandoMail LOBE: Resolve-PandoDid LOCAL MISS '$requestedDid' — no parent, replying not found"
            $notFoundEnvelope = [ordered]@{
                typ  = 'application/didcomm-plain+json'
                id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
                thid = $msg.WireId
                type = 'did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/Reply-DidDocument'
                from = $SVRN7.LocalDid
                to   = @($SVRN7.LocalDid)
                body = [ordered]@{
                    requestedDid = $requestedDid
                    found        = $false
                    svrn7Name    = ''
                }
            } | ConvertTo-Json -Compress -Depth 3
            return [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $notFoundEnvelope)
        }

        # Forward the resolve request to the parent TDA using this request's WireId as the
        # Identity LOBE's requestId/originalRequestId. That inter-TDA relay chain (requestId/
        # originalRequesterDid/originalRequestId as body fields) is unchanged — see
        # Svrn7.Identity.0.8.0.psm1. Invoke-Svrn7DidResolveResponse will push Reply-DidDocument
        # back to WebSocket when the response arrives, this time via envelope thid.
        Write-Verbose "PandoMail LOBE: Resolve-PandoDid LOCAL MISS '$requestedDid' → escalating to '$parentDid'"
        $fwdEnvelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            type = 'did:drn:svrn7.net/protocols/Svrn7.Identity.0.8.0/did-resolve-request'
            from = $SVRN7.LocalDid
            to   = @($parentDid)
            body = [ordered]@{
                requestedDid         = $requestedDid
                requestId            = $msg.WireId
                originalRequesterDid = $SVRN7.LocalDid
                originalRequestId    = $msg.WireId
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new($parentEndpoint, $fwdEnvelope)
    }
}

# ── Invoke-PandoMailListSent ──────────────────────────────────────────────────

function Invoke-PandoMailListSent {
    <#
    .SYNOPSIS
        Handles a List-OutboundEmails query and replies with a Get-PandoOutbox response.

    .DESCRIPTION
        Queries the local inbox for Enqueue-PandoMail messages (emails sent from PandoMail UI)
        and delivers a Get-PandoOutbox DIDComm message to the sender's endpoint over WebSocket.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-OutboundEmails
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoOutbox

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering Get-PandoOutbox over WebSocket.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: List-OutboundEmails message $MessageDid not found."
            return $null
        }

        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $limit = 50
        if ($body.PSObject.Properties['limit']) { $limit = [int]$body.limit }

        $sent = $SVRN7.ListSentEmailsAsync($limit).GetAwaiter().GetResult()

        $emailList = @(foreach ($e in $sent) {
            $eBody = $e.PackedPayload | ConvertFrom-Json -ErrorAction SilentlyContinue
            [ordered]@{
                messageDid = $e.Id
                senderDid  = $SVRN7.LocalDid
                subject    = Get-BodyField $eBody 'subject' '(no subject)'
                fromHeader = if (Get-BodyField $eBody 'senderDisplay' '') { Get-BodyField $eBody 'senderDisplay' '' } else { $SVRN7.LocalDid }
                toHeader   = if (Get-BodyField $eBody 'recipientDisplay' '') { Get-BodyField $eBody 'recipientDisplay' '' } else { Get-BodyField $eBody 'recipientDid' '' }
                ccHeader   = Get-BodyField $eBody 'ccDisplay' ''
                receivedAt = $e.ReceivedAt.ToString('o')
            }
        })

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoOutbox'
            from = $SVRN7.LocalDid
            to   = @($msg.FromDid)
            body = [ordered]@{
                emails = $emailList
                count  = $emailList.Count
            }
        } | ConvertTo-Json -Compress -Depth 5

        Write-Verbose "PandoMail LOBE: List-OutboundEmails returning $($emailList.Count) sent messages."
        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

# ── Invoke-PandoMailListDeadLetters ───────────────────────────────────────────

function Invoke-PandoMailListDeadLetters {
    <#
    .SYNOPSIS
        Handles a List-DeadLetters query and replies with a Get-PandoDeadLetters response.

    .DESCRIPTION
        Returns pending dead-letter records (failed outbound deliveries) over the WebSocket hub.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-DeadLetters
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoDeadLetters

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering Get-PandoDeadLetters over WebSocket.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "PandoMail LOBE: List-DeadLetters message $MessageDid not found."
            return $null
        }

        $records = $SVRN7.ListDeadLettersAsync().GetAwaiter().GetResult()

        # PackedMessage is the authentic, unpacked Signal-PandoMail envelope built by
        # Enqueue-PandoMail before delivery was attempted — same plaintext rfc5322Body shape
        # as a normal Inbox message, so it's parsed identically. This is the real content the
        # user composed; $r.PeerEndpoint/LastError describe the failure, not the message.
        $emailList = @(foreach ($r in $records) {
            $rEnvelope  = $r.PackedMessage | ConvertFrom-Json -ErrorAction SilentlyContinue
            $rInnerBody = if ($rEnvelope) { Get-BodyField $rEnvelope 'body' $null } else { $null }
            $rfc5322    = if ($rInnerBody) { Get-BodyField $rInnerBody 'rfc5322Body' '' } else { '' }

            [ordered]@{
                messageDid = $r.Id
                senderDid  = $SVRN7.LocalDid
                subject    = if ($rfc5322) { (Get-Rfc5322Header -Raw $rfc5322 -Header 'Subject') } else { "FAILED: $($r.LastError)" }
                fromHeader = if ($rfc5322) { (Get-Rfc5322Header -Raw $rfc5322 -Header 'From') } else { $SVRN7.LocalDid }
                toHeader   = if ($rfc5322) { (Get-Rfc5322Header -Raw $rfc5322 -Header 'To') } else { $r.PeerEndpoint }
                ccHeader   = if ($rfc5322) { (Get-Rfc5322Header -Raw $rfc5322 -Header 'Cc') } else { '' }
                receivedAt = $r.FailedAt.ToString('o')
            }
        })

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Get-PandoDeadLetters'
            from = $SVRN7.LocalDid
            to   = @($msg.FromDid)
            body = [ordered]@{
                emails = $emailList
                count  = $emailList.Count
            }
        } | ConvertTo-Json -Compress -Depth 5

        Write-Verbose "PandoMail LOBE: List-DeadLetters returning $($emailList.Count) dead-letter record(s)."
        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

# ── Invoke-PandoMailQueryFolderCounts ────────────────────────────────────────

function Invoke-PandoMailQueryFolderCounts {
    <#
    .SYNOPSIS
        Handles a Query-FolderCounts request from PandoMail and pushes current counts.

    .DESCRIPTION
        Called by PandoMail on connect to populate folder tree annotations from
        existing data without requiring the user to click each folder first.
        Delegates entirely to New-FolderCountsNotification.

        Protocol (inbound):  did:drn:svrn7.net/protocols/PandoMail.0.8.0/Query-FolderCounts
        Protocol (outbound): did:drn:svrn7.net/protocols/PandoMail.0.8.0/Notify-FolderCounts

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )
    process {
        # New-FolderCountsNotification lives in Svrn7.SMTPEmail.0.8.0 (this LOBE's
        # dependency) — it counts state owned by that generic transport LOBE.
        New-FolderCountsNotification
    }
}

Export-ModuleMember -Function @(
    'Invoke-PandoMailList',
    'Invoke-PandoMailSend',
    'Invoke-PandoMailResolveDid',
    'Get-TdaDid',
    'Invoke-Svrn7EmailGetEmailBody',
    'Invoke-PandoMailListSent',
    'Invoke-PandoMailListDeadLetters',
    'Invoke-PandoMailQueryFolderCounts'
)
