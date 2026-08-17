# Pando.Diagnostics LOBE — Debug & Testing Guide
#
# This guide covers building, running, and testing the Pando.Diagnostics example LOBE
# end-to-end against a live TDA.  It is self-contained from the point of starting the TDA
# in debug mode.  See docs/DEBUG.ps1 for general TDA background and additional scenarios.
#
# ---
#
# Prerequisites
#
# - PowerShell 7 (pwsh.exe) is required.  The VS Developer PowerShell defaults to
#   Windows PowerShell 5.1, which is missing required .NET types.
#
#   Verify before running any command:

$PSVersionTable.PSVersion   # Major must be 7

#   To open a PowerShell 7 terminal from VS 2022/2026:
#   Tools → Options → Environment → Terminal → Add — set shell to
#   C:\Program Files\PowerShell\7\pwsh.exe.
#
# ---
#
# Step 1 — Build
#
# From the repo root in PowerShell 7:

Write-Host "--- Step 1 — Build ---"
Set-Location C:/SVRN7/repos/SVRN7
dotnet build src/Svrn7.TDA/Svrn7.TDA.csproj

# Verify the three LOBE files were copied to the output:

Set-Location src/Svrn7.TDA/bin/Debug/net8.0
Get-ChildItem lobes/Pando.Diagnostics.0.1.0

# Expected:
#
# Pando.Diagnostics.Impl.0.1.0.psm1
# Pando.Diagnostics.0.1.0.lobe.json
# Pando.Diagnostics.0.1.0.psm1

# Verify Pando.Diagnostics is NOT listed in lobes.config.json. It is a JIT LOBE —
# lobes.config.json's schema has only an "eager" array (no "jit" key exists at all);
# JIT LOBEs are auto-discovered at startup from their *.lobe.json descriptor and are
# never listed in this file. See LobeManager.cs, LobeConfig doc comment.

Get-Content lobes/lobes.config.json | Select-String "Pando"

# Expected: no output. If "Pando" appears here, it was added to the eager array by
# mistake — Pando.Diagnostics is meant to load JIT, on first dispatch (Step 2).
#
# ---
#
# Step 2 — Start the TDA
#
# In the TDA output folder (src/Svrn7.TDA/bin/Debug/net8.0):

Write-Host "--- Step 2 — Start the TDA ---"
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# Expected startup log:
#
# info: DIDCommMessageSwitchboard[0]
#       DIDCommMessageSwitchboard: drain loop started.
# info: KestrelListenerService[0]
#       TDA Kestrel listener started on port 8443 (h2c).
#
# Pando.Diagnostics is a JIT LOBE — it is not loaded at startup.  It is imported
# into the runspace the first time a Pando.Diagnostics.0.1.0/Query-TOD message arrives.
#
# ---
#
# Step 3 — Load the send helper
#
# Session note: This step only needs to be done once per PowerShell session.
# If your PowerShell terminal is still open from a previous run and the module is
# already imported, skip this step and go straight to Step 5.
#
# In a separate PowerShell 7 terminal, navigate to the TDA output folder and import the
# Federation module to get Send-LocalDIDCommMessage:

Write-Host "--- Step 3 — Load the send helper ---"
Set-Location C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0
Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1

# ---
#
# Step 4 — Bootstrap (first run only)
#
# Persistence note: The LiteDB databases survive TDA restarts — you only need to
# run this step once, or again after a full reset (Remove-Item mem\svrn7.db ...).
# The federation and society records created here persist until the databases are
# explicitly deleted.  The only thing that does not persist between sessions is
# the Import-Module call in your PowerShell send terminal — repeat Step 3 if you
# started a new PowerShell session.  If the terminal is still open, skip it.
#
# Skip this step if the TDA database already has a federation and society record.
#
# Open a second PowerShell 7 terminal, navigate to the output folder, and run the
# bootstrap sequence.  (The TDA must be running in the first terminal throughout.)

Write-Host "--- Step 4 — Bootstrap (first run only) ---"
Set-Location C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0
Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1

# 4.1 — Generate the federation key pair (one-time)

Write-Host "--- Step 4.1 — Generate the federation key pair ---"
$federationKp = New-Svrn7KeyPair
Write-Host "Public key : $($federationKp.PublicKeyHex)"

# 4.2 — Initialise the federation

Write-Host "--- Step 4.2 — Initialise the federation ---"
$body = @{
    federationDid        = "did:drn:societytest.svrn7.net"
    federationName       = "Web 7.0 SOVRON Foundation"
    publicKeyHex         = $federationKp.PublicKeyHex
    primaryDidMethodName = "drn"
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/initialize-federation"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log: Federation initialised: did:drn:societytest.svrn7.net
#
# 4.3 — Verify the federation was created

Write-Host "--- Step 4.3 — Verify the federation was created ---"
$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/federation-query"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = "{}"
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log:
# Invoke-Web7FederationQuery: replying to did:drn:societytest.svrn7.net
#
# Expected reply body (delivered back to the local TDA inbox and logged):
# {
#   "found": true,
#   "federationDid": "did:drn:societytest.svrn7.net",
#   "federationName": "Web 7.0 SOVRON Foundation",
#   "primaryDidMethodName": "drn",
#   "totalSupplyGrana": ...,
#   "currentEpoch": 0,
#   "isActive": true
# }
#
# If "found": false the federation init in §4.2 did not complete — check the TDA log
# for errors from Invoke-Web7FederationInit.
#
# 4.4 — Register a new society

Write-Host "--- Step 4.4 — Register a new society ---"
$societyKp = New-Svrn7KeyPair

$body = @{
    societyDid           = "did:drn:bindloss.svrn7.net"
    publicKeyHex         = $societyKp.PublicKeyHex
    societyName          = "Bindloss Alberta"
    primaryDidMethodName = "bindloss"
    drawAmountGrana      = 1000000000000
    overdraftCeilingGrana= 10000000000000
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/register-society"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log: Society registered: did:drn:bindloss.svrn7.net (Bindloss Alberta)
#
# 4.5 — List all societies in the federation

Write-Host "--- Step 4.5 — List all societies in the federation ---"
Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1

# NOTE (verified against source): Invoke-Web7SocietyList (Svrn7.Federation.0.8.0.psm1)
# does not read "replyEndpoint" or any other body field — the field below has no effect
# and is not required (the .lobe.json descriptor says "No body fields required").
# The reply endpoint is resolved solely from the sender ("from") DID's registered
# DIDCommMessaging service entry, via Resolve-SocietySenderEndpoint (Svrn7.Common.0.8.0.psm1).
# The federation DID created in Step 4.2 above has no registered service endpoint unless
# a "serviceEndpointUrl" field is added to that step's body — without it, this step (and
# §4.3) will produce a "cannot resolve endpoint" warning instead of the reply shown below.
$body = @{
    replyEndpoint = "http://localhost:8443"
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/society-list"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log:
#
# info:  Switchboard: routing ... (type=.../federation/1.0/society-list) → Invoke-Web7SocietyList [Svrn7.Federation]
# info:    [PS Info] Invoke-Web7SocietyList: 1 society/societies, replying to http://localhost:8443
# info:    Switchboard: outbound delivered to http://localhost:8443/didcomm (202).
#
# Expected society-list-result reply body:
#
# {
#   "count": 1,
#   "activeCount": 1,
#   "societies": [
#     {
#       "societyDid":           "did:drn:bindloss.svrn7.net",
#       "societyName":          "Bindloss Alberta",
#       "primaryDidMethodName": "bindloss",
#       "isActive":             true,
#       "registeredAt":         "2026-05-30T..."
#     }
#   ],
#   "queriedAt": "2026-05-30T..."
# }
#
# ---
#
# Step 5 — Send a Query-TOD message (minimal body)
#
# The simplest test. NOTE (verified against source): Invoke-PandoDiagnosticsDateQuery
# ignores the request body entirely — it is parsed but never referenced — so it always
# attempts to resolve a reply endpoint from the sender DID's registered DIDCommMessaging
# service entry, regardless of body content. Whether delivery actually succeeds depends
# only on that DID Document lookup, not on a "replyEndpoint" field (see Step 6 note).

Write-Host "--- Step 5 — Send a Query-TOD message (no reply) ---"
$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/Query-TOD"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = "{}"
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected response: Status: Accepted
#
# Expected TDA log:
#
# info:  Switchboard: routing did:drn:societytest.svrn7.net/inbox/msg/<id>
#            (type=did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/Query-TOD)
#            → Invoke-PandoDiagnosticsDateQuery [Pando.Diagnostics]
# info:    [PS Info] Pando.Diagnostics: serverUtc=2026-05-30T... epoch=0
# warn:    [PS Warning] Invoke-PandoDiagnosticsDateQuery: no reply endpoint — result not delivered.
#
# The [PS Warning] line confirms the handler ran successfully. It appears because the
# sender DID has no registered DIDCommMessaging service endpoint — not because of
# anything in the request body (the body is never read; see note above).
#
# ---
#
# Step 6 — Send a Query-TOD message (same body-ignoring handler)
#
# NOTE (verified against source): as in Step 5, Invoke-PandoDiagnosticsDateQuery does
# not read "replyEndpoint" or any other body field — this field has no effect. The
# reply path below succeeds or fails based solely on whether the sender DID
# ("did:drn:societytest.svrn7.net") has a registered DIDCommMessaging service endpoint,
# which requires "serviceEndpointUrl" to have been included in Step 4.2's body.

Write-Host "--- Step 6 — Send a Query-TOD message (with reply endpoint) ---"
$body = @{
    replyEndpoint = "http://localhost:8443"
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/Query-TOD"
    from = "did:drn:societytest.svrn7.net"
    to   = @("did:drn:societytest.svrn7.net")
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Body $msg

# Expected TDA log additions:
#
# info:    [PS Info] Pando.Diagnostics: serverUtc=2026-05-30T... epoch=0
# info:    Switchboard: outbound delivered to http://localhost:8443/didcomm (202).
#
# Expected Issue-TOD reply body:
#
# {
#   "serverUtc":       "2026-05-30T...",
#   "serverUtcOffset": "+00:00",
#   "currentEpoch":    0,
#   "respondedAt":     "2026-05-30T..."
# }
#
# ---
#
# Step 7 — Verify Get-TDADate standalone (no TDA required)
#
# Pando.Diagnostics.Impl.0.1.0.psm1 can be imported and tested in isolation — no TDA,
# no assemblies, no database needed.

Write-Host "--- Step 7 — Verify Get-TDADate standalone ---"
Import-Module .\lobes\Pando.Diagnostics.0.1.0\Pando.Diagnostics.Impl.0.1.0.psm1

$now = Get-TDADate
Write-Host "Server time : $($now.ToString('o'))"
Write-Host "Type        : $($now.GetType().FullName)"

# Expected:
#
# Server time : 2026-05-30T...+00:00
# Type        : System.DateTimeOffset
#
# ---
#
# Step 8 — Reset between test runs

Write-Host "--- Step 8 — Reset between test runs ---"
# (Stop the TDA first — Ctrl+C in the TDA terminal)
Remove-Item -Path "mem\svrn7-msg.db", "mem\svrn7-msg.db-log" -ErrorAction SilentlyContinue
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# For a full reset (clears federation and society records too):

Remove-Item -Path "mem\*.db" -ErrorAction SilentlyContinue
dotnet .\Svrn7.TDA.dll --port 8443 --name MyTDA

# After a full reset, repeat Step 4 before testing the LOBE.
#
# ---
#
# Common Error Conditions
#
# | Symptom                                                      | Cause                                                   | Fix                                           |
# |--------------------------------------------------------------|---------------------------------------------------------|-----------------------------------------------|
# | No LOBE registered for @type .../Pando.Diagnostics.0.1.0/Query-TOD | Pando.Diagnostics.0.1.0.lobe.json missing from output, or protocol URI mismatch | Verify Step 1 |
# | The term 'Get-TDADate' is not recognized                     | Pando.Diagnostics.Impl.0.1.0.psm1 not found at $PSScriptRoot | Verify all three files are in lobes/Pando.Diagnostics.0.1.0/ |
# | Invoke-PandoDiagnosticsDateQuery: message '...' not found.   | Message expired from cache before handler ran           | Retry; check inbox store                      |
# | [PS Warning] no reply endpoint — result not delivered.       | Expected when replyEndpoint absent and sender has no DID Document | Normal for Step 5 (no-reply variant)   |
# | serverUtc and respondedAt are identical                      | Normal — both calls run within microseconds             | Not an error                                  |
