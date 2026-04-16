# Svrn7.TDA — Debug & Testing Guide

## Overview

- Single inbound endpoint: `POST http://localhost:8443/didcomm`
- Protocol: **HTTP/2 cleartext (h2c)** — the server only speaks HTTP/2; HTTP/1.1 requests are rejected
- No TLS cert configured → cleartext development mode (see `Program.cs` and `KestrelListenerService.cs`)
- `UnpackAsync` has a **plaintext branch**: if the JSON body has a `"type"` property at the root, it passes through without decryption — no keys needed for dev testing
- A valid message returns **202 Accepted** and is enqueued; the Switchboard routes it asynchronously

---

## Log Level

Set in `Program.cs` `ConfigureLogging`:

```csharp
logging.SetMinimumLevel(LogLevel.Trace);   // verbose
logging.SetMinimumLevel(LogLevel.Information); // normal
```

---

## PowerShell Testing

### Step 1 — Connectivity check

```powershell
Test-NetConnection localhost -Port 8443
```

---

### Step 2 — Send a plaintext DIDComm message (PowerShell 7.3+)

`-HttpVersion 2.0` is required — the server rejects HTTP/1.1.

```powershell
$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = [System.Guid]::NewGuid().ToString("N")
    type = "did:drn:svrn7.net/protocols/transfer/1.0/request"
    from = "did:test:sender"
    to   = @("did:drn:alpha.svrn7.net")
    body = "{}"
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
    -Uri "http://localhost:8443/didcomm" `
    -Body $msg `
    -ContentType "application/didcomm-plain+json" `
    -HttpVersion 2.0
```

Expected: `202 Accepted`

---

### Step 3 — PowerShell 7.2 or earlier (no `-HttpVersion`): use `HttpClient` directly

```powershell
$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestVersion = [System.Version]::new(2, 0)
$client.DefaultVersionPolicy  = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact

$body = @{
    typ  = "application/didcomm-plain+json"
    id   = [System.Guid]::NewGuid().ToString("N")
    type = "did:drn:svrn7.net/protocols/transfer/1.0/request"
    from = "did:test:sender"
    to   = @("did:drn:alpha.svrn7.net")
    body = "{}"
} | ConvertTo-Json

$content = [System.Net.Http.StringContent]::new(
    $body,
    [System.Text.Encoding]::UTF8,
    "application/didcomm-plain+json"
)

$response = $client.PostAsync("http://localhost:8443/didcomm", $content).GetAwaiter().GetResult()
"Status: $($response.StatusCode)"
$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
```

---

## Available Protocol URIs

| `type` URI | LOBE Handler |
|---|---|
| `did:drn:svrn7.net/protocols/transfer/1.0/request` | `Invoke-Svrn7IncomingTransfer` |
| `did:drn:svrn7.net/protocols/transfer/1.0/order` | `Invoke-Svrn7IncomingTransfer` |
| `did:drn:svrn7.net/protocols/transfer/1.0/order-receipt` | `Confirm-Svrn7Settlement` |
| `did:drn:svrn7.net/protocols/onboard/1.0/request` | `Register-Svrn7CitizenInSociety` |

Any other `type` value is enqueued (202) but the Switchboard will log an unroutable message — visible at `LogLevel.Trace`.

---

## Response Codes

| Code | Meaning |
|---|---|
| `202 Accepted` | Message unpacked and enqueued successfully |
| `400 Bad Request` | Empty body, invalid JSON, or DIDComm unpack failed |

---

## Tracing PowerShell Cmdlet Execution

### What is logged and where

At `LogLevel.Information`, the Switchboard logs the cmdlet name and LOBE when it dispatches a message:

```
Switchboard: routing {Did} (type={Type}) → {EP} [{LOBE}]
```

At `LogLevel.Trace`, `InvokeCmdletPipelineAsync` in `DIDCommMessageSwitchboard.cs` additionally logs
cmdlet start, completion, and all PowerShell streams forwarded to the .NET logger:

```
[Trace] PS invoke: Invoke-Svrn7IncomingTransfer -MessageDid did:tda:...
[Trace]   [PS Verbose] ...   ← Write-Verbose inside the .psm1
[Debug]   [PS Debug] ...     ← Write-Debug
[Info]    [PS Info] ...      ← Write-Information / Write-Host
[Warn]    [PS Warning] ...   ← Write-Warning
[Trace] PS complete: Invoke-Svrn7IncomingTransfer → 1 result(s).
```

PS stream forwarding happens after `ps.Invoke()` returns — output is in order but not
streaming line-by-line during execution. For live output from long-running cmdlets, wire
`ps.Streams.Verbose.DataAdded` events in `InvokeCmdletPipelineAsync`.

---

## Scenario A — Create first Society "bindloss"

The Society is not created via a DIDComm message. It is initialised at TDA startup via
configuration. Follow these steps to bring up a fresh TDA for the Bindloss Society.

### A.1 — Configure `appsettings.json`

Create or edit `appsettings.json` beside the `Svrn7.TDA` binary:

```json
{
  "Svrn7": {
    "SocietyDid":  "did:drn:bindloss.svrn7.net",
    "FederationDid": "did:drn:foundation.svrn7.net",
    "DbPath":      "./data/svrn7.db",
    "DidsDbPath":  "./data/svrn7-dids.db",
    "VcsDbPath":   "./data/svrn7-vcs.db",
    "InboxDbPath": "./data/svrn7-inbox.db",
    "SchemasDbPath": "./data/svrn7-schemas.db"
  },
  "Tda": {
    "SocietyDid":  "did:drn:bindloss.svrn7.net",
    "ListenPort":  8443,
    "RequireMutualTls": false,
    "AcceptSelfSigned": true,
    "LobesConfigPath": "./lobes/lobes.config.json"
  }
}
```

Or set environment variables instead (`.NET` colon → `__` in env):

```powershell
$env:Svrn7__SocietyDid   = "did:drn:bindloss.svrn7.net"
$env:Svrn7__FederationDid = "did:drn:foundation.svrn7.net"
$env:Tda__SocietyDid     = "did:drn:bindloss.svrn7.net"
$env:Tda__ListenPort     = "8443"
$env:Tda__RequireMutualTls    = "false"
$env:Tda__AcceptSelfSigned    = "true"
```

### A.2 — First-run startup

```powershell
dotnet run --project src/Svrn7.TDA
```

On first run the TDA:
1. Opens (or creates) the five LiteDB files under `./data/`.
2. Registers the `Svrn7SocietyOptions` with `SocietyDid = did:drn:bindloss.svrn7.net`.
3. Loads eager LOBEs: `Svrn7.Common`, `Svrn7.Federation`, `Svrn7.Society`, `Svrn7.UX`.
4. Starts the Switchboard drain loop and Kestrel on port 8443.

Expected console lines (LogLevel.Information):

```
info: DIDCommMessageSwitchboard[0]
      DIDCommMessageSwitchboard: drain loop started.
info: KestrelListenerService[0]
      TDA Kestrel listener started on port 8443 (h2c).
```

### A.3 — Verify the Society is running (PowerShell module)

In a separate PowerShell 7 session, import the Society module and query the running instance:

```powershell
Import-Module ./src/Svrn7.TDA/lobes/Svrn7.Federation.psm1
Import-Module ./src/Svrn7.TDA/lobes/Svrn7.Society.psm1

Initialize-Svrn7Federation

Connect-Svrn7Society `
    -SocietyDid     "did:drn:bindloss.svrn7.net" `
    -FederationDid  "did:drn:foundation.svrn7.net" `
    -DidMethodNames @("bindloss") `
    -DbPath         "./data"

Get-Svrn7OwnSociety | Select-Object SocietyDid, CurrentEpoch
```

Expected output:

```
SocietyDid                   CurrentEpoch
----------                   ------------
did:drn:bindloss.svrn7.net   0
```

### A.4 — Check overdraft baseline

```powershell
Get-Svrn7OverdraftStatus
```

Expected (fresh Society, no registrations yet):

```
SocietyDid                   Status
----------                   ------
did:drn:bindloss.svrn7.net   Clean
```

---

## Scenario B — Register first citizen "mwherman" via DIDComm

Citizen registration is driven by the `onboard/1.0/request` DIDComm protocol.
The Switchboard routes the inbound message to Agent 2 (Onboarding LOBE), which calls
`Register-Svrn7CitizenInSociety` and returns an `onboard/1.0/receipt`.

### B.1 — Generate key material for "mwherman"

Run once and save the output. The private key must be stored securely by the citizen's
own TDA — the Society stores only the public key.

```powershell
Import-Module ./src/Svrn7.TDA/lobes/Svrn7.Federation.psm1
Initialize-Svrn7Federation

# Generate secp256k1 signing key pair
$kp  = New-Svrn7KeyPair

# Derive citizen DID under the "bindloss" method
$did = New-Svrn7Did -KeyPair $kp -MethodName "bindloss"

Write-Host "Citizen DID : $($did.Did)"
Write-Host "Public key  : $($kp.PublicKeyHex)"
Write-Host "Private key : $($kp.PrivateKeyHex)   <-- store this securely, never share"
```

Example output (your values will differ — keys are randomly generated):

```
Citizen DID : did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy
Public key  : 0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798
Private key : <32-byte hex — keep secret>
```

### B.2 — Send the onboarding request via DIDComm (PowerShell 7.3+)

Replace `$citizenDid` and `$publicKeyHex` with the values from step B.1.

The `body` field is a JSON-string (stringified) — the plaintext unpack branch passes it
through to `PackedPayload` which `ConvertFrom-Web7OnboardRequest` parses with
`ConvertFrom-Json`.

```powershell
$citizenDid   = "did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy"
$publicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"

$body = @{
    citizenDid   = $citizenDid
    publicKeyHex = $publicKeyHex
    displayName  = "mwherman"
} | ConvertTo-Json -Compress

$msg = @{
    typ  = "application/didcomm-plain+json"
    id   = [System.Guid]::NewGuid().ToString("N")
    type = "did:drn:svrn7.net/protocols/onboard/1.0/request"
    from = $citizenDid
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
    -Uri "http://localhost:8443/didcomm" `
    -Body $msg `
    -ContentType "application/didcomm-plain+json" `
    -HttpVersion 2.0
```

Expected: `202 Accepted`

### B.3 — PowerShell 7.2 or earlier (no `-HttpVersion`): use `HttpClient` directly

```powershell
$citizenDid   = "did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy"
$publicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"

$body = @{
    citizenDid   = $citizenDid
    publicKeyHex = $publicKeyHex
    displayName  = "mwherman"
} | ConvertTo-Json -Compress

$msgJson = @{
    typ  = "application/didcomm-plain+json"
    id   = [System.Guid]::NewGuid().ToString("N")
    type = "did:drn:svrn7.net/protocols/onboard/1.0/request"
    from = $citizenDid
    to   = @("did:drn:bindloss.svrn7.net")
    body = $body
} | ConvertTo-Json

$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestVersion = [System.Version]::new(2, 0)
$client.DefaultVersionPolicy  = [System.Net.Http.HttpVersionPolicy]::RequestVersionExact

$content = [System.Net.Http.StringContent]::new(
    $msgJson,
    [System.Text.Encoding]::UTF8,
    "application/didcomm-plain+json"
)

$response = $client.PostAsync("http://localhost:8443/didcomm", $content).GetAwaiter().GetResult()
"Status: $($response.StatusCode)"
$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
```

### B.4 — Verify registration in the TDA log

With `LogLevel.Trace`, look for the Agent 2 pipeline output:

```
[Info]  Switchboard: routing did:drn:bindloss.svrn7.net/inbox/msg/<id>
        (type=did:drn:svrn7.net/protocols/onboard/1.0/request) → ConvertFrom-Web7OnboardRequest [Svrn7.Onboarding]
[Trace] PS invoke: Agent2-Onboarding.ps1 -MessageDid did:drn:...
[Trace]   [PS Verbose] Agent 2 / Onboarding: processing did:drn:...
[Trace]   [PS Verbose] Agent 2 / Onboarding: registering citizen did:bindloss:3J98...
[Info]    [PS Info] Agent 2 / Onboarding: citizen did:bindloss:3J98... registered. Endowment: 1000000000 grana.
[Trace] PS complete: Agent2-Onboarding.ps1 → 1 result(s).
```

### B.5 — Verify registration via PowerShell module

```powershell
# Re-use the Connect-Svrn7Society session from Scenario A.3

Test-Svrn7SocietyMember -Did "did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy"
```

Expected:

```
Did        : did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy
SocietyDid : did:drn:bindloss.svrn7.net
IsMember   : True
```

```powershell
Get-Svrn7SocietyMembers | Select-Object MemberCount, MemberDids
```

Expected after first registration:

```
MemberCount MemberDids
----------- ----------
          1 {did:bindloss:3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy}
```

### B.6 — Error: duplicate registration

Sending the same `onboard/1.0/request` a second time (same `citizenDid`) results in
a `202 Accepted` at the HTTP layer (the Switchboard always enqueues), but Agent 2 will
log an error and return an `onboard/1.0/receipt` with `success: false`:

```
[Error] Agent 2 / Onboarding: failed for did:drn:.../inbox/msg/<id> — CitizenAlreadyRegisteredException
```

This is expected. `Register-Svrn7CitizenInSociety` is not idempotent.

---

## Scenario C — Common error conditions

| Symptom | Cause | Fix |
|---|---|---|
| `400 Bad Request` on POST /didcomm | Body is empty, not valid JSON, or missing `type` field at root | Add `"type"` key to the message root |
| `202` but log shows `No LOBE registered for @type` | `type` URI does not match any registered protocol | Check `lobes.config.json` and the `.lobe.json` protocol URIs |
| `202` but log shows `CitizenAlreadyRegisteredException` | Citizen DID already registered | Expected — use a new key pair and DID |
| `202` but log shows `SocietyEndowmentDepletedException` | Society overdraft ceiling reached | Check `Get-Svrn7OverdraftStatus`; await Federation top-up |
| Agent 2 log: `No DIDComm service endpoint for <DID>` | Citizen DID document has no `DIDComm` service entry | Register the citizen's DID document before sending the receipt |
| Switchboard epoch gate log warning | `type` URI requires a higher epoch than `CurrentEpoch` | Only transfer/order URIs are epoch-gated (require Epoch 1+) |
