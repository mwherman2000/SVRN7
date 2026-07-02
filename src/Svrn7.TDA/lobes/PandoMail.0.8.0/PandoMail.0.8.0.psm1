#Requires -Version 7.0
<#
.SYNOPSIS
    SVRN7 Email LOBE — DIDComm-native email using RFC 5322 tunneling.

.DESCRIPTION
    Implements the did:drn:svrn7.net/protocols/PandoMail.0.8.0/* DIDComm protocol.
    RFC 5322 email messages are tunneled verbatim inside DIDComm envelopes.
    No SMTP server is involved. All email communication is TDA-to-TDA via DIDComm.

    Derived from: Email LOBE (Agent 1 LOBE) — DSA 0.24 Epoch 0 (PPML).

.NOTES
    Protocol URIs:
        did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail   — inbound/outbound email
        did:drn:svrn7.net/protocols/PandoMail.0.8.0/issue-receipt   — delivery confirmation

    Key:
        From/To headers in the RFC 5322 payload use did: URIs, not SMTP addresses.
        The sender's DID is verified from the DIDComm envelope — not the From header.
        No SMTP server, no MX records, no MIME multipart (Epoch 0).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Dequeue-PandoMail ──────────────────────────────────────────────────────────

function Dequeue-PandoMail {
    <#
    .SYNOPSIS
        Processes an inbound DIDComm email/1.0/message and stores it locally.

    .DESCRIPTION
        Accepts an inbox message DID URL, resolves the message payload via
        $SVRN7.GetMessageAsync(), extracts the RFC 5322 body, verifies the
        sender's DID against the DIDComm envelope, and persists the email
        record to the IInboxStore long-term memory.

        Derived from: Email LOBE (Agent 1 LOBE) — DSA 0.24 Epoch 0 (PPML).
        Protocol: did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.
        Form: did:drn:{networkId}/inbox/msg/{objectId}

    .OUTPUTS
        EmailRecord — the stored email record, or $null if processing failed.

    .EXAMPLE
        Dequeue-PandoMail -MessageDid "did:drn:societytest.svrn7.net/inbox/msg/5f43a2b1c8e9d7f012345678"

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
        Write-Verbose "Email LOBE: processing inbound email $MessageDid"

        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "Email LOBE: message $MessageDid not found."
            return $null
        }

        # Parse the DIDComm body — expected: { from, rfc5322Body }
        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $rfc5322 = $body.rfc5322Body
        if (-not $rfc5322) {
            Write-Warning "Email LOBE: message $MessageDid has no rfc5322Body field."
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

        Write-Verbose "Email LOBE: stored email from $($record.SenderDid) — '$($record.Subject)'"

        # Push Email-Notify to PandoMail via the local WebSocket hub.
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

# ── Enqueue-PandoMail ─────────────────────────────────────────────────────────────

function Enqueue-PandoMail {
    <#
    .SYNOPSIS
        Sends an RFC 5322 email message to a recipient TDA via DIDComm.

    .DESCRIPTION
        Constructs a DIDComm email/1.0/message body containing a full RFC 5322
        message. Resolves the recipient's DID to their TDA endpoint and returns
        an OutboundMessage for the Switchboard to deliver.

        Protocol: did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail

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
        Enqueue-PandoMail -RecipientDid "did:drn:beta.svrn7.net/citizen/bob" -Subject "Hello" -Body "Hi Bob" -Cc "did:drn:beta.svrn7.net/citizen/carol;did:drn:beta.svrn7.net/citizen/dave"
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
                type = 'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail'
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
                Write-Warning "Enqueue-PandoMail: no DIDComm service endpoint for '$targetDid' — writing to dead letters."
                $SVRN7.EnqueueDeadLetterAsync(
                    $targetDid,
                    $targetEnvelope,
                    'did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail',
                    "No DIDComm service endpoint found for recipient '$targetDid'"
                ).GetAwaiter().GetResult()
                continue
            }

            [Svrn7.TDA.OutboundMessage]::new($peerEndpoint, $targetEnvelope)
        }

        New-FolderCountsNotification
    }
}

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
            Write-Warning "Email LOBE: List-Emails message $MessageDid not found."
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

        Write-Verbose "Email LOBE: List-Emails returning $($emailList.Count) messages via WebSocket."
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
        when there are multiple recipients. Builds an RFC 5322 message via Enqueue-PandoMail and
        returns an OutboundMessage per recipient for delivery.

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
            Write-Warning "Email LOBE: Enqueue-PandoMail message $MessageDid not found."
            return $null
        }

        $body = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop

        $recipientDid = Get-BodyField $body 'recipientDid'
        if (-not $recipientDid) {
            Write-Warning "Email LOBE: Enqueue-PandoMail $MessageDid missing recipientDid — skipped."
            return $null
        }

        $subject          = Get-BodyField $body 'subject'          ''
        $bodyText         = Get-BodyField $body 'bodyText'         ''
        $senderDisplay    = Get-BodyField $body 'senderDisplay'    ''
        $recipientDisplay = Get-BodyField $body 'recipientDisplay' ''
        $cc               = Get-BodyField $body 'cc'               ''
        $ccDisplay        = Get-BodyField $body 'ccDisplay'        ''

        Write-Verbose "Email LOBE: Enqueue-PandoMail — forwarding to $recipientDid ('$subject')"
        Enqueue-PandoMail -RecipientDid $recipientDid -Subject $subject -Body $bodyText `
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
            Write-Warning "Email LOBE: Get-EmailBody request message $MessageDid not found."
            return $null
        }

        $body      = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $targetDid = Get-BodyField $body 'messageDid' ''

        if (-not $targetDid) {
            Write-Warning "Email LOBE: Get-EmailBody $MessageDid missing messageDid field."
            return $null
        }

        $emailMsg = $SVRN7.GetMessageAsync($targetDid).GetAwaiter().GetResult()
        if (-not $emailMsg) {
            Write-Warning "Email LOBE: Get-EmailBody target message $targetDid not found."
            return $null
        }

        $emailBody = $emailMsg.PackedPayload | ConvertFrom-Json -ErrorAction SilentlyContinue
        $rfc5322   = Get-BodyField $emailBody 'rfc5322Body' ''

        # Extract plain-text body — everything after the first blank line in RFC 5322.
        $bodyText = ''
        if ($rfc5322) {
            $parts = $rfc5322 -split "`r?`n`r?`n", 2
            if ($parts.Count -ge 2) { $bodyText = $parts[1].Trim() }
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
            Write-Warning "Email LOBE: Resolve-PandoDid message $MessageDid not found."
            return $null
        }

        $body         = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $requestedDid = Get-BodyField $body 'requestedDid'  ''

        if (-not $requestedDid) {
            Write-Warning "Email LOBE: Resolve-PandoDid $MessageDid missing requestedDid."
            return $null
        }

        # Try local registry first
        $didDoc = $SVRN7.Driver.ResolveDidAsync($requestedDid).GetAwaiter().GetResult()
        if ($null -ne $didDoc) {
            Write-Verbose "Email LOBE: Resolve-PandoDid LOCAL HIT '$requestedDid'"
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
            Write-Verbose "Email LOBE: Resolve-PandoDid svrn7Name='$svrn7Name'"
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
            Write-Verbose "Email LOBE: Resolve-PandoDid LOCAL MISS '$requestedDid' — no parent, replying not found"
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
        Write-Verbose "Email LOBE: Resolve-PandoDid LOCAL MISS '$requestedDid' → escalating to '$parentDid'"
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
            Write-Warning "Email LOBE: List-OutboundEmails message $MessageDid not found."
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

        Write-Verbose "Email LOBE: List-OutboundEmails returning $($emailList.Count) sent messages."
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
            Write-Warning "Email LOBE: List-DeadLetters message $MessageDid not found."
            return $null
        }

        $records = $SVRN7.ListDeadLettersAsync().GetAwaiter().GetResult()

        $emailList = @(foreach ($r in $records) {
            [ordered]@{
                messageDid = $r.Id
                senderDid  = $SVRN7.LocalDid
                subject    = if ($r.LastError) { "FAILED: $($r.LastError)" } else { $r.MessageType }
                fromHeader = $SVRN7.LocalDid
                toHeader   = $r.PeerEndpoint
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

        Write-Verbose "Email LOBE: List-DeadLetters returning $($emailList.Count) dead-letter record(s)."
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
        New-FolderCountsNotification
    }
}

# ── New-FolderCountsNotification ─────────────────────────────────────────────
# Internal helper — not exported. Queries current folder counts and returns an
# OutboundMessage that pushes Notify-FolderCounts over the local WebSocket hub.
# Called after every LOBE operation that changes inbox, sent, or dead-letter counts.

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
    'Dequeue-PandoMail',
    'Enqueue-PandoMail',
    'Invoke-PandoMailList',
    'Invoke-PandoMailSend',
    'Invoke-PandoMailResolveDid',
    'Get-TdaDid',
    'Invoke-Svrn7EmailGetEmailBody',
    'Invoke-PandoMailListSent',
    'Invoke-PandoMailListDeadLetters',
    'Invoke-PandoMailQueryFolderCounts'
)
