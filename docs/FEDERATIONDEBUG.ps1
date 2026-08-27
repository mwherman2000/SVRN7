# Web 7.0 Pando — Federation TDA Debug Guide
#
# Covers launching a Federation TDA and performing the full Federation bootstrap:
# initialising the federation record, querying it, and registering the first Society.
#
# Run this guide first.  SOCIETYDEBUG.ps1 requires the Federation to be initialised
# before it starts.
#
# ---
#
# Overview
#
# The Federation TDA is the root tier of the Web 7.0 Pando hierarchy.  It holds the
# shared ledger, registers Societies, and serves society-list responses to Wanderers
# seeking to join a Society.  One Federation TDA exists per network deployment.
#
# Key protocols handled by a Federation TDA:
#
# | Protocol URI                                                              | Handler                     |
# |---------------------------------------------------------------------------|------------------------------|
# | did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/initialize-federation | Invoke-Web7FederationInit   |
# | did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/federation-query      | Invoke-Web7FederationQuery  |
# | did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/register-society      | Invoke-Web7RegisterSociety  |
# | did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/society-list          | Invoke-Web7SocietyList      |
#
# ---
#
# Prerequisites
#
# - PowerShell 7 (pwsh.exe):

$PSVersionTable.PSVersion   # Major must be 7

# - Solution built:

Set-Location C:/SVRN7/repos/SVRN7
dotnet build src/Svrn7.TDA/Svrn7.TDA.csproj

# ---
#
# Working Directory
#
# All commands assume:

Set-Location C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0

# Run this once at the start of every session.
#
# ---
#
# Step 1 — Launch the Federation TDA

Write-Host "--- Step 1 — Launch the Federation TDA ---"
dotnet .\Svrn7.TDA.dll --port 8441 --name Federation

# Expected startup banner (first run):
#
# ────────────────────────────────────────────────────────────────────────────────
#   SVRN7 Trusted Digital Assistant (TDA)  v0.8.0
#   Web 7.0 Foundation — https://svrn7.net
# ────────────────────────────────────────────────────────────────────────────────
#   TDA Name    : Federation
#   First run   : yes — Wanderer identity created
#   Role        : Wanderer
#   Agent DID   : did:drn:wanderer.svrn7.net/agent/1.0/<genesis-hash>
#   Listen port : 8441
# ────────────────────────────────────────────────────────────────────────────────
#   Federation  : (not yet initialised — see §E.0 to initialise)
#   Societies   : (not yet initialised)
# ────────────────────────────────────────────────────────────────────────────────
#
# ---
#
# Step 2 — Load the send helper (separate PowerShell terminal)
#
# Open a second PowerShell 7 terminal and set the working directory:

Write-Host "--- Step 2 — Load the send helper ---"
Set-Location C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0

# Import the LOBE that provides Send-LocalDIDCommMessage, and admin-tools for
# New-Svrn7KeyPair (moved out of Svrn7.Federation.0.8.0.psm1 — see
# docs/LOBEGUIDE.md "Division of Responsibility" for why):

Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1
Import-Module .\admin-tools\Svrn7.AdminTools\Svrn7.AdminTools.psm1

# Invoke-RestMethod -HttpVersion 2.0 does not work with cleartext HTTP/2 (h2c):
# PowerShell uses HttpVersionPolicy.RequestVersionOrLower, which falls back to
# HTTP/1.1 — rejected by the server.  Send-LocalDIDCommMessage enforces HTTP/2 via
# RequestVersionExact.
#
# ---
#
# E.0 — Initialise the Federation
#
# Sent once, before any Societies are registered.  Idempotent — safe to repeat.
#
# E.0.1 — Generate the federation governance key pair
#
# This is a one-time operation.  The private key must be stored in a key vault
# or HSM and never placed in config files.  The public key is recorded permanently
# in the federation record.

Write-Host "--- E.0.1 — Generate the federation governance key pair ---"
$federationKp  = New-Svrn7KeyPair
$federationDid = (New-Svrn7Did -KeyPair $federationKp -Role Federation).Did

Write-Host "Federation DID : $federationDid"
Write-Host "Public key     : $($federationKp.PublicKeyHex)"
Write-Host "Private key    : $($federationKp.PrivateKeyHex)   <-- store securely, never share"

# Example output (your values will differ):
#
# Federation DID : did:drn:federation.svrn7.net/federation/1.0/<genesis-hash>
# Public key     : 0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798
# Private key    : 18e14a7b5a...  <-- store securely, never share
#
# E.0.2 — Send initialize-federation
#
# federationDid, federationName, and publicKeyHex are required (Assert-BodyFields in
# Invoke-Web7FederationInit — src\Svrn7.TDA\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1).
# The DID method is always 'drn' — there is no primaryDidMethodName body field; the
# handler passes a literal 'drn' to CreateDidDocument regardless of what is sent.

Write-Host "--- E.0.2 — Send initialize-federation ---"
$body = @{
    federationDid  = $federationDid
    federationName = "Web 7.0 SOVRON Foundation"
    publicKeyHex   = $federationKp.PublicKeyHex
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/initialize-federation"
    from = $federationDid
    to   = @($federationDid)
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8441 -Body $msg

# Expected TDA log:
#
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0]
#       Switchboard: routing ... (type=.../Svrn7.Federation.0.8.0/initialize-federation)
#           → Invoke-Web7FederationInit [Svrn7.Federation]
# info: Svrn7.Federation.Svrn7Driver[0]
#       Federation initialised: <federationDid> (Web 7.0 SOVRON Foundation), supply 1000000000000000 grana
#
# The "Federation initialised: ..." line is logged directly by Svrn7Driver.InitialiseFederationAsync
# (src\Svrn7.Federation\Svrn7Driver.cs) under its own ILogger category — it is NOT a
# forwarded PowerShell stream, so it does not carry the "[PS Info]" prefix or the
# Svrn7.TDA.DIDCommMessageSwitchboard[0] category used for genuine Write-Information output.
# If the sender DID resolves to a DIDCommMessaging endpoint, a second line follows:
#
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0]
#       [PS Info] Invoke-Web7FederationInit: federation '<federationDid>' initialised, replying to <endpoint>
#
# Reply body (initialize-federation-result) delivered to the sender — only the fields
# actually assembled by Invoke-Web7FederationInit (there is no primaryDidMethodName field):
#
# {
#   "federationDid":      "<federationDid>",
#   "federationName":     "Web 7.0 SOVRON Foundation",
#   "totalSupplyGrana":   1000000000000000,
#   "alreadyInitialised": false,
#   "initialisedAt":      "2026-..."
# }
#
# ---
#
# E.1 — Query the Federation record
#
# Verifies the federation was initialised correctly.  Also works before initialisation
# — returns found: false.

Write-Host "--- E.1 — Query the Federation record ---"
$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/federation-query"
    from = $federationDid
    to   = @($federationDid)
    body = "{}"
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8441 -Body $msg

# Expected TDA log:
#
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0]
#       Switchboard: routing ... (type=.../Svrn7.Federation.0.8.0/federation-query)
#           → Invoke-Web7FederationQuery [Svrn7.Federation]
#
# Reply body (federation-query-result), as currently assembled by the handler:
#
# {
#   "found":                    true,
#   "federationDid":            "<federationDid>",
#   "federationName":           "Web 7.0 SOVRON Foundation",
#   "primaryDidMethodName":     "drn",
#   "totalSupplyGrana":         1000000000000000,
#   "endowmentPerSocietyGrana": 0,
#   "currentEpoch":             0,
#   "isActive":                 true,
#   "createdAt":                "2026-...",
#   "queriedAt":                "2026-..."
# }
#
# ---
#
# E.2 — Register the first Society
#
# The Society TDA (or a bootstrap script) sends register-society to the Federation TDA.
# serviceEndpointUrl is required so the Federation can create the Society's DID Document
# and deliver the register-society-result reply.
#
# Invoke-Web7RegisterSociety handles the request on the Federation TDA.

Write-Host "--- E.2 — Register the first Society ---"
$societyKeyPair = New-Svrn7KeyPair

# societyDid below is NOT used to derive the registered Society's DID — Invoke-Web7RegisterSociety
# (Svrn7.Federation.0.8.0.psm1) always derives it server-side as
# did:drn:federation.svrn7.net/{societyName}/1.0/{Blake3(publicKeyHex)}. The field must still be
# present in the body: the handler's diagnostic log line reads $body.societyDid directly
# (Svrn7.Federation.0.8.0.psm1:1512, not guarded by Assert-BodyFields/Get-BodyField), and
# Set-StrictMode throws if the property is absent. Only publicKeyHex and societyName are
# actually required (Assert-BodyFields). primaryDidMethodName is not read anywhere in the
# handler — the DID method is always 'drn' — so it has been omitted here.
#
# societyName becomes the DID path segment verbatim — keep it lowercase with no spaces.
#
# GranaPerSvrn7 = 1,000,000 (src\Svrn7.Core\Svrn7Constants.cs) — 1 SVRN7 = 1,000,000 grana.

$body = @{
    societyDid            = "did:drn:federation.svrn7.net/bindloss/1.0/<placeholder-ignored>"
    publicKeyHex          = $societyKeyPair.PublicKeyHex
    societyName           = "bindloss"
    serviceEndpointUrl    = "http://localhost:8442/didcomm"   # Society TDA endpoint
    drawAmountGrana       = 1000000000000     # 1,000,000 SVRN7
    overdraftCeilingGrana = 10000000000000    # 10,000,000 SVRN7
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/register-society"
    from = "did:drn:wanderer.svrn7.net/agent/1.0/<genesis-hash>"   # the Society TDA's own bootstrap Wanderer DID
    to   = @($federationDid)
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8441 -Body $msg

# Expected TDA log (Federation TDA):
#
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0]
#       Switchboard: routing ... (type=.../Svrn7.Federation.0.8.0/register-society)
#           → Invoke-Web7RegisterSociety [Svrn7.Federation]
# warn: ...
#       RegisterSocietyAsync: FoundationPrivateKey not configured — VTC credential
#       skipped for did:drn:federation.svrn7.net/bindloss/1.0/<genesis-hash> (development mode)
# info: Svrn7.TDA.DIDCommMessageSwitchboard[0]
#       [PS Info] Invoke-Web7RegisterSociety: registered '<value of body.societyDid — see note above>'
#
# The Federation TDA delivers register-society-result to the Society TDA at
# http://localhost:8442/didcomm.  See SOCIETYDEBUG.ps1 §E.2r for what the Society TDA
# does on receipt.
#
# Reply body (register-society-result) — only the fields actually assembled by
# Invoke-Web7RegisterSociety (there is no primaryDidMethodName field):
#
# {
#   "societyDid":            "did:drn:federation.svrn7.net/bindloss/1.0/<genesis-hash>",
#   "societyName":           "bindloss",
#   "societyDidDocument":    { "Did": "did:drn:federation.svrn7.net/bindloss/1.0/<genesis-hash>", ... },
#   "federationDid":         "<federationDid>",
#   "federationEndpointUrl": "http://localhost:8441/didcomm",
#   "federationDidDocument": { "Did": "<federationDid>", ... },
#   "drawAmountGrana":       1000000000000,
#   "overdraftCeilingGrana": 10000000000000,
#   "success":               true,
#   "registeredAt":          "2026-..."
# }
#
# ---
#
# Resetting the Federation TDA
#
# Stop the TDA before deleting any database file — LiteDB holds an exclusive
# write lock for the lifetime of the process.

Write-Host "--- Resetting the Federation TDA ---"
# Delete all Federation TDA data (port 8441)
Remove-Item -Recurse -Force 8441\mem -ErrorAction SilentlyContinue

# Or use --reset at startup (equivalent):
dotnet .\Svrn7.TDA.dll --port 8441 --name Federation --reset

# ---
#
# Available Protocol URIs — Federation TDA
#
# | type URI                                             | Handler                    | Direction |
# |------------------------------------------------------|----------------------------|-----------|
# | .../Svrn7.Federation.0.8.0/initialize-federation    | Invoke-Web7FederationInit  | inbound   |
# | .../Svrn7.Federation.0.8.0/federation-query         | Invoke-Web7FederationQuery | inbound   |
# | .../Svrn7.Federation.0.8.0/register-society         | Invoke-Web7RegisterSociety | inbound   |
# | .../Svrn7.Federation.0.8.0/society-list             | Invoke-Web7SocietyList     | inbound   |
#
# Full URI prefix: did:drn:svrn7.net/protocols/
#
# ---
#
# Response Codes
#
# | Code                       | Meaning                                                                        |
# |----------------------------|--------------------------------------------------------------------------------|
# | 202 Accepted               | Message unpacked and enqueued successfully                                     |
# | 400 Bad Request            | Empty body, invalid JSON, or missing type field                                |
# | 415 Unsupported Media Type | Content-Type not application/didcomm-encrypted+json or application/didcomm-plain+json |
# | 403 Forbidden              | Plaintext message with @type not in PlaintextDiscoveryProtocols                |
#
# ---
#
# Log Level
#
# Set in appsettings.json:
# "Svrn7.TDA.DIDCommMessageSwitchboard": "Debug"
#
# Or in Program.cs ConfigureLogging:
# logging.SetMinimumLevel(LogLevel.Trace);   // verbose
# logging.SetMinimumLevel(LogLevel.Information); // normal
#
# ---
#
# Tracing Cmdlet Execution
#
# At LogLevel.Information, the Switchboard logs the cmdlet name and LOBE on dispatch:
# Switchboard: routing {Did} (type={Type}) → {EP} [{LOBE}]
#
# At LogLevel.Trace, it additionally logs cmdlet start, completion, and all PowerShell
# streams forwarded to the .NET logger:
#
# [Trace] PS invoke: Invoke-Web7FederationInit -MessageDid did:drn:federation.svrn7.net/inbox/msg/...
# [Info]    [PS Info] Invoke-Web7FederationInit: federation '<federationDid>' initialised, replying to <endpoint>
# [Trace] PS complete: Invoke-Web7FederationInit → 1 result(s).
#
# ---
#
# Error Reference
#
# | Symptom                                      | Cause                                        | Fix                                              |
# |----------------------------------------------|----------------------------------------------|--------------------------------------------------|
# | 400 Bad Request                              | Missing type field at root                   | Add "type" key to the message root               |
# | 202 but no routing log                       | type URI does not match any LOBE protocol    | Check .lobe.json URIs; use exact match           |
# | [PS Info] already initialised                | initialize-federation sent twice             | Idempotent — safe to ignore                      |
# | Society TDA shows no register-society-result | Federation could not reach serviceEndpointUrl | Confirm Society TDA is running on port 8442      |
