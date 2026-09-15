# Svrn7.Email LOBE — Debug & Testing Guide
#
# This guide covers building, running, and testing the Svrn7.Email LOBE end-to-end
# against a live TDA.  It is self-contained from the point of starting the TDA in debug
# mode.  See docs/LOBEDEBUG.ps1 for the federation/society bootstrap sequence and
# general TDA background.
#
# ---
#
# Prerequisites
#
# - PowerShell 7 (pwsh.exe) is required.

$PSVersionTable.PSVersion   # Major must be 7

# - The TDA database must already have a federation and society record.
#   If starting fresh, complete Steps 1–4 of docs/LOBEDEBUG.ps1 first.
#
# ---
#
# Step 1 — Build
#
# From the repo root in PowerShell 7:

Write-Host "--- Step 1 — Build ---"
Set-Location C:/SVRN7/repos/SVRN7
dotnet build src/Svrn7.TDA/Svrn7.TDA.csproj

# Verify the Email LOBE files are in the output:

Set-Location src/Svrn7.TDA/bin/Debug/net8.0
Get-ChildItem lobes/PandoMail.0.8.0

# Expected:
#
# PandoMail.0.8.0.lobe.json
# PandoMail.0.8.0.psd1
# PandoMail.0.8.0.psm1

# Svrn7.Email (module PandoMail.0.8.0) is a JIT LOBE. JIT LOBEs are auto-discovered
# from their own *.lobe.json and are never listed in lobes.config.json — that file
# lists only the eager LOBEs (Svrn7.Common, Svrn7.Federation, Svrn7.Society, Svrn7.UX).
# Confirm it is absent from the eager list:

Get-Content lobes/lobes.config.json | Select-String "PandoMail"

# Expected: no output — Svrn7.Email/PandoMail is not eager-loaded.
#
# ---
#
# Step 2 — Start the TDA
#
# In the TDA output folder (src/Svrn7.TDA/bin/Debug/net8.0):

Write-Host "--- Step 2 — Start the TDA ---"
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# Svrn7.Email is a JIT LOBE — it is not imported at startup.  It is loaded into
# the runspace the first time a PandoMail.0.8.0/* message arrives.  Expect a one-time
# import log line on first delivery.
#
# ---
#
# Step 3 — Load the send helper
#
# In a separate PowerShell 7 terminal:

Write-Host "--- Step 3 — Load the send helper ---"
Set-Location C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0
Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1

# This gives you Send-LocalDIDCommMessage for all subsequent steps.
#
# ---
#
# Step 4 — Send a test email (no reply)
#
# The inbound body carries two fields: from (authoritative sender DID) and
# rfc5322Body (complete RFC 5322 message text).  The sender's DID must appear
# in both the outer DIDComm envelope (from) and the inner body (from) — the
# handler uses the body field as the canonical sender identity.

Write-Host "--- Step 4 — Send a test email ---"
$rfc5322 = @"
From: Web 7.0 Foundation <did:drn:foundation.svrn7.net>
To: Bindloss Alberta <did:drn:bindloss.svrn7.net>
Subject: Hello from the Foundation
Date: $(([datetime]::UtcNow).ToString('ddd, dd MMM yyyy HH:mm:ss')) +0000
MIME-Version: 1.0
Content-Type: text/plain; charset=utf-8

This is a test email sent via DIDComm.
No SMTP server was involved.
"@

$body = @{
    from        = "did:drn:foundation.svrn7.net"
    to          = "did:drn:bindloss.svrn7.net"
    rfc5322Body = $rfc5322
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail"
    from = "did:drn:foundation.svrn7.net"
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected response: Sent to ws://localhost:8443/localcomm-ws (<N> bytes)
# (Send-LocalDIDCommMessage returns this confirmation string on success — see
# Svrn7.Common.0.8.0.psm1. It does not return an HTTP-style "Status: Accepted".)
#
# Expected TDA log (timestamps vary):
#
# 20:49:13.432 info: Svrn7.TDA.DIDCommMessageSwitchboard[0] Switchboard: routing ... (type=.../PandoMail.0.8.0/Signal-PandoMail) → Dequeue-PandoMail [Svrn7.Email]
# 20:49:13.441 dbug: Svrn7.TDA.LobeManager[0] LobeManager: EnsureLoadedAsync - JIT '...\lobes\PandoMail.0.8.0\PandoMail.0.8.0.psm1'.
# 20:49:13.512 info: Svrn7.TDA.LobeManager[0] LobeManager: import complete - ...\PandoMail.0.8.0.psm1
# 20:49:13.518 dbug: Svrn7.TDA.DIDCommMessageSwitchboard[0]   [PS Verbose] Email LOBE: stored email from did:drn:foundation.svrn7.net — 'Hello from the Foundation'
# 20:49:13.519 dbug: Svrn7.Society.LiteInboxStore[0] Inbox: message ... marked Processed
#
# The [PS Verbose] line confirms the handler parsed the RFC 5322 subject correctly.
# Import-Module -Force runs on every JIT dispatch — the import lines appear each time.
#
# ---
#
# Step 5 — Send a second email

Write-Host "--- Step 5 — Send a second email ---"
$body = @{
    from        = "did:drn:foundation.svrn7.net"
    to          = "did:drn:bindloss.svrn7.net"
    rfc5322Body = "From: did:drn:foundation.svrn7.net`r`nTo: did:drn:bindloss.svrn7.net`r`nSubject: Second message`r`nDate: $(([datetime]::UtcNow).ToString('ddd, dd MMM yyyy HH:mm:ss')) +0000`r`nMIME-Version: 1.0`r`nContent-Type: text/plain; charset=utf-8`r`n`r`nSecond test."
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail"
    from = "did:drn:foundation.svrn7.net"
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# The import lines appear again — JIT LOBEs reimport on every dispatch (see Step 4 note).
#
# ---
#
# Step 6 — Send an email delivery receipt
#
# The issue-receipt protocol is also handled by Dequeue-PandoMail.  A receipt
# body conventionally carries originalMessageId and deliveredAt:

Write-Host "--- Step 6 — Send an email delivery receipt ---"
$body = @{
    from              = "did:drn:foundation.svrn7.net"
    to                = "did:drn:bindloss.svrn7.net"
    originalMessageId = "did:drn:svrn7.net/didcomm/msg/abc123"
    deliveredAt       = [datetimeoffset]::UtcNow.ToString('o')
    rfc5322Body       = "From: did:drn:foundation.svrn7.net`r`nTo: did:drn:bindloss.svrn7.net`r`nSubject: Delivery receipt`r`nDate: $(([datetime]::UtcNow).ToString('ddd, dd MMM yyyy HH:mm:ss')) +0000`r`nMIME-Version: 1.0`r`nContent-Type: text/plain; charset=utf-8`r`n`r`nYour message was delivered."
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/issue-receipt"
    from = "did:drn:foundation.svrn7.net"
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log:
#
# 20:49:14.101 info: ... Switchboard: routing ... (type=.../PandoMail.0.8.0/issue-receipt) → Dequeue-PandoMail [Svrn7.Email]
# 20:49:14.104 dbug: ...   [PS Verbose] Email LOBE: stored email from did:drn:foundation.svrn7.net — 'Delivery receipt'
#
# Both Signal-PandoMail and issue-receipt route to Dequeue-PandoMail —
# the handler stores whichever arrives and returns the record for pipeline chaining.
#
# ---
#
# Step 7 — Inspect the email record
#
# Dequeue-PandoMail returns a hashtable with these fields:
#
# | Field        | Value                                                              |
# |--------------|--------------------------------------------------------------------|
# | MessageDid   | TDA resource DID URL — did:drn:societytest.svrn7.net/inbox/msg/{objectId} |
# | MessageId    | LiteDB ObjectId hex of the inbox record                            |
# | SenderDid    | from field of the DIDComm body (application-level sender, not envelope) |
# | ReceivedAt   | ISO 8601 UTC timestamp                                             |
# | Rfc5322Body  | Complete RFC 5322 text as a single string                          |
# | Subject      | Extracted Subject: header value, or $null if absent                |
# | FromHeader   | Extracted From: header value, or $null                             |
# | ToHeader     | Extracted To: header value, or $null                               |
#
# Sender identity note: SenderDid is extracted from the DIDComm message body's
# from field — not from the RFC 5322 From: header and not from the DIDComm
# envelope's from field.  The RFC 5322 header is treated as display metadata only.
#
# ---
#
# Step 8 — Missing rfc5322Body field (error path)
#
# Send a malformed message with no rfc5322Body to see how the handler behaves.
#
# NOTE (verified against source): Dequeue-PandoMail reads $body.rfc5322Body via plain
# dot-notation (PandoMail.0.8.0.psm1) rather than the Assert-BodyFields/Get-BodyField
# helpers. Set-StrictMode -Version Latest is active at the top of this LOBE, and under
# StrictMode a missing property on a ConvertFrom-Json object throws — it does not
# return $null. (Confirmed directly: accessing an absent property on such an object
# under Set-StrictMode -Version Latest raises "The property '...' cannot be found on
# this object.") So this message does NOT degrade gracefully with a warning — it
# throws, the Switchboard's dispatch try/catch marks it Failed, and it is retried
# (Signal-PandoMail is not in Svrn7Constants.TransactionalProtocols, so retry: true,
# up to NonTransactionalMaxAttempts) rather than left Processed.

Write-Host "--- Step 8 — Missing rfc5322Body field (error path) ---"
$body = @{
    from = "did:drn:foundation.svrn7.net"
    to   = "did:drn:bindloss.svrn7.net"
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Signal-PandoMail"
    from = "did:drn:foundation.svrn7.net"
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log:
#
# fail: Svrn7.TDA.DIDCommMessageSwitchboard[0] Switchboard: dispatch failed for message ... (attempt 1, transactional=False).
#       ... The property 'rfc5322Body' cannot be found on this object. Verify that the property exists. ...
#
# The message is marked Failed and retried — not a clean Processed completion.
#
# ---
#
# Step 9 — Reset between test runs

Write-Host "--- Step 9 — Reset between test runs ---"
# (Stop the TDA first — Ctrl+C in the TDA terminal)
Remove-Item -Path "mem\svrn7-msg.db", "mem\svrn7-msg.db-log" -ErrorAction SilentlyContinue
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# For a full reset (clears all records):

Remove-Item -Path "mem\*.db" -ErrorAction SilentlyContinue
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# After a full reset, repeat the bootstrap in docs/LOBEDEBUG.ps1 Step 4 before
# re-running these steps.
#
# ---
#
# Step 10 — List stored emails (List-Emails protocol)
#
# Send a List-Emails query to retrieve stored email messages from the TDA inbox.
# Invoke-PandoMailList replies with a Get-PandoMails message pushed over the local
# WebSocket hub (ws://local/localcomm-ws) — the same Local UI Attachment Point
# pattern used by every PandoMail query/reply pair. This is not an HTTP DIDComm
# delivery to a resolved DID Document endpoint.

Write-Host "--- Step 10 — List stored emails ---"
$body = @{
    limit = 10
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-Emails"
    from = "did:drn:foundation.svrn7.net"
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log:
#
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0] Switchboard: routing ... (type=.../List-Emails) → Invoke-PandoMailList [Svrn7.Email]
# dbug: ...   [PS Verbose] Email LOBE: List-Emails returning N messages via WebSocket.
#
# The Get-PandoMails reply is pushed as a DIDComm plaintext message over the local
# WebSocket hub (ws://local/localcomm-ws) — see Invoke-PandoMailList in PandoMail.0.8.0.psm1.
# Each email entry:
#
# | Field      | Value                                                        |
# |------------|--------------------------------------------------------------|
# | messageDid | TDA resource DID URL of the inbox record                     |
# | senderDid  | Authoritative sender DID (from DIDComm envelope from field)  |
# | subject    | Extracted Subject: header, or $null                          |
# | fromHeader | Extracted From: header, or $null                             |
# | toHeader   | Extracted To: header, or $null                               |
# | receivedAt | ISO 8601 UTC timestamp                                       |
#
# Note: The reply is pushed over the local WebSocket hub, not delivered via HTTP DIDComm
# to a resolved DID Document endpoint. Send-LocalDIDCommMessage closes its WebSocket
# connection immediately after sending, so a standalone PowerShell session will not
# observe the Get-PandoMails push — connect a WebSocket client (e.g. PandoMail itself)
# to ws://localhost:{port}/localcomm-ws to see it.
#
# ---
#
# Common Error Conditions
#
# | Symptom                                      | Cause                                                 | Fix                                                      |
# |----------------------------------------------|-------------------------------------------------------|----------------------------------------------------------|
# | No LOBE registered for @type .../PandoMail.0.8.0/Signal-PandoMail | @type doesn't match a registered protocol prefix/exact match, or PandoMail.0.8.0.lobe.json/.psm1 missing from output | Verify Step 1; check the "uri" list in PandoMail.0.8.0.lobe.json |
# | PropertyNotFoundException: 'rfc5322Body' cannot be found on this object | Body missing rfc5322Body key — Dequeue-PandoMail reads it via dot-notation under Set-StrictMode | Include rfc5322Body in the DIDComm body JSON (see Step 4); message is marked Failed/retried, not Processed (see Step 8) |
# | [PS Warning] message ... not found           | Message expired from cache before handler ran         | Retry; increase MaxMessageAgeSeconds in TdaOptions       |
# | Subject shows $null in verbose log           | RFC 5322 Subject: header absent or misspelled         | Verify header name casing (Subject: not subject:)        |
# | Import lines appear on every message         | JIT LOBEs run Import-Module -Force each dispatch      | Expected — see TDA-001a backlog                          |
