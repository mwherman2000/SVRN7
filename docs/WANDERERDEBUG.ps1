# SVRN7 — Wanderer TDA Debug Guide
#
# Launches two Wanderer TDA instances (W5 on 8445, W6 on 8446) and runs a
# `Query-TOD` / `Issue-TOD` round-trip between them using the `Pando.Diagnostics`
# LOBE (installed JIT on first reference).
#
# Per-identity runtime storage (docs/AGENTWALLET.md, SECURITY.md): on first run
# each instance derives a secp256k1 + X25519 key pair from a fresh 12-word BIP39
# phrase, creates a DID Document, and writes an ENCRYPTED wallet:
#
#     ~/.web7-pando/<name>-<genesisHash8>/agent-identity.wallet
#
# unlocked with $env:PANDO_WALLET_PASSWORD. Databases (mem/*.db) and the
# per-instance lobes/ folder live under the same directory. No federation or
# society registration is required for the Query-TOD walkthrough.
#
# ---
#
# Prerequisites
#
# - PowerShell 7 (`pwsh.exe`).

$PSVersionTable.PSVersion   # Major must be 7

# - Build the TDA and populate the machine-level LOBE library. The simplest way
#   is the testnet script, which builds and copies dist/*.nupkg into
#   ~/.web7-pando/lobe-library/:

# Set-Location C:/SVRN7/repos/SVRN7
# ./tools/Initialize-Testnet.ps1 -SkipBuild:$false   # (or just: dotnet build + copy dist\*.nupkg to ~/.web7-pando/lobe-library)

# Manual equivalent:
Set-Location C:/SVRN7/repos/SVRN7
dotnet build src/Svrn7.TDA/Svrn7.TDA.csproj -c Debug
New-Item -ItemType Directory -Force (Join-Path $HOME '.web7-pando/lobe-library') | Out-Null
Copy-Item dist/*.nupkg -Destination (Join-Path $HOME '.web7-pando/lobe-library') -Force

# - Confirm the Pando.Diagnostics package is available for JIT install. It is a
#   JIT LOBE — never in lobes.config.json's eager list; the TDA installs it from
#   lobe-library/ the first time a Query-TOD message is dispatched.
Test-Path (Join-Path $HOME '.web7-pando/lobe-library/Pando.Diagnostics.0.1.0.nupkg')

# Expected: True

# - Shared wallet password for this walkthrough (any value; every W5/W6 command
#   below inherits it).
$env:PANDO_WALLET_PASSWORD = 'wanderer-debug'

# ---
#
# Terminal layout
#
# | Terminal       | Purpose                                                  |
# |----------------|--------------------------------------------------------- |
# | **A — W5**     | Runs W5 (port 8445); watch log output here              |
# | **B — W6**     | Runs W6 (port 8446); watch log output here              |
# | **C — Sender** | Sends DIDComm messages; reads identity.meta.json        |
#
# ---
#
# Helper — launches a titled pwsh window running the TDA. Uses -EncodedCommand so
# the window title (spaces/brackets/colons) cannot be corrupted by -ArgumentList
# quoting. The wallet password is passed through the child environment.

function Start-TdaWindow {
    param(
        [Parameter(Mandatory)] [string] $Title,
        [Parameter(Mandatory)] [string] $WorkDir,
        [Parameter(Mandatory)] [string] $DotnetArgs
    )
    $pw = $env:PANDO_WALLET_PASSWORD
    $script = "`$env:PANDO_WALLET_PASSWORD = '$pw'; Set-Location `"$WorkDir`"; " +
              "`$Host.UI.RawUI.WindowTitle = `"$Title`"; dotnet `".\Svrn7.TDA.dll`" $DotnetArgs"
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($script))
    Start-Process pwsh.exe -ArgumentList "-NoExit -EncodedCommand $encoded"
}

$bin = 'C:/SVRN7/repos/SVRN7/src/Svrn7.TDA/bin/Debug/net8.0'

# ---
#
# Step 1 — Start W5 and W6 (Terminals A and B)

Clear-Host
Write-Host "--- Step 1 — Start W5 and W6 ---"

# --reset deletes each identity's whole runtime directory
# (~/.web7-pando/w5-<hash8>/ , w6-<hash8>/) so both boot with fresh, unique DIDs.
Start-TdaWindow -Title 'W5 [Wanderer]:8445' -WorkDir $bin -DotnetArgs '--name W5 --port 8445 --reset'
Start-TdaWindow -Title 'W6 [Wanderer]:8446' -WorkDir $bin -DotnetArgs '--name W6 --port 8446 --reset'
Pause

# Production / staging: add `--federationdomain svrn7.net` to auto-discover the
# Federation TDA endpoint via drn.directory DNS at startup (shown as `Fed Endpoint`
# in the banner, exposed as $SVRN7.FederationEndpointUrl in every LOBE runspace).

# ---
#
# Step 2 — Verify the startup banners (Terminals A and B)
#
# First run — expected banner (version is git-height based, e.g. 0.8.13+<commit>):
#
# ────────────────────────────────────────────────────────────────────────────────
#   SVRN7 Trusted Digital Assistant (TDA)  v0.8.<n>+<commit8>
#   Web 7.0 Foundation — https://svrn7.net
# ────────────────────────────────────────────────────────────────────────────────
#   TDA Name    : W5
#   TDA Role    : Wanderer
#   Bootstrap   : first run — new identity created
#   Agent DID   : did:drn:wanderer.svrn7.net/agent/1.0/<genesis-hash-W5>
#   Data root   : C:\Users\<you>\.web7-pando
#   Instance    : C:\Users\<you>\.web7-pando\w5-<hash8>
#   Endpoint    : http://localhost:8445/didcomm  (auto-selected)   # only if the port was auto-picked
# ────────────────────────────────────────────────────────────────────────────────
#   RECOVERY PHRASE — write this down now, it is shown only once:
#     <12 words>
# ────────────────────────────────────────────────────────────────────────────────
#
# Each eager LOBE (Svrn7.Common / Federation / Society / UX) is installed from
# lobe-library/ into ~/.web7-pando/w5-<hash8>/lobes/ during startup, then imported.

# ---
#
# Step 3 — Read the Wanderer DIDs (Terminal C)
#
# The DID is in each instance's cleartext identity.meta.json (the DID Document
# itself is in the encrypted svrn7-dids.db — read it with `db-shell` if needed).

Write-Host "--- Step 3 — Read the Wanderer DIDs ---"
$pando = Join-Path $HOME '.web7-pando'
$w5Meta = Get-ChildItem $pando -Directory -Filter 'w5-*' | Select-Object -First 1 |
    ForEach-Object { Get-Content (Join-Path $_.FullName 'identity.meta.json') -Raw | ConvertFrom-Json }
$w6Meta = Get-ChildItem $pando -Directory -Filter 'w6-*' | Select-Object -First 1 |
    ForEach-Object { Get-Content (Join-Path $_.FullName 'identity.meta.json') -Raw | ConvertFrom-Json }
$w5Did = $w5Meta.did
$w6Did = $w6Meta.did
Write-Host "W5 DID: $w5Did"
Write-Host "W6 DID: $w6Did"

# ---
#
# Step 4 — Import the send helper (Terminal C)
#
# Send-LocalDIDCommMessage ships in Svrn7.Common (an eager LOBE), so it is already
# installed under W6's instance lobes/ folder.

Write-Host "--- Step 4 — Import the send helper ---"
$w6Common = Get-ChildItem $pando -Directory -Filter 'w6-*' | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'lobes/Svrn7.Common.0.8.0/Svrn7.Common.0.8.0.psm1' }
Import-Module $w6Common -Force

# ---
#
# Step 5 — Send Query-TOD from W6 to itself (Terminal C)
#
# W6's own DID Document is in its local registry, so the Issue-TOD reply is
# delivered back to W6 without federation.

Write-Host "--- Step 5 — Send Query-TOD from W6 to itself ---"
$msg = @{
    typ  = 'application/didcomm-plain+json'
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = 'did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/Query-TOD'
    from = $w6Did
    to   = @($w6Did)
    body = '{}'
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8446 -Body $msg

# Expected from Terminal C:  Status: Accepted

# ---
#
# Step 6 — Verify W6 processed Query-TOD and replied (Terminal B)
#
# On the FIRST dispatch, W6 installs the JIT LOBE from the library:
#
#   info: Svrn7.TDA.LobeManager[0]
#         LobeManager: JIT-installed 'Pando.Diagnostics' 0.1.0 on first reference to @type '…/Query-TOD'.
#
# Then, for this and every later dispatch (Import-Module -Force per dispatch — hot
# update, ~30 ms, tracked as TDA-001a):
#
#   info: … LobeManager: import complete — …\Pando.Diagnostics.0.1.0.psm1
#   info: … [PS Info] Pando.Diagnostics: serverUtc=… epoch=0
#   info: … Switchboard: outbound delivered to http://localhost:8446/didcomm (202).
#
# ---
#
# Step 7 — Verify W6 received the Issue-TOD reply (Terminal B)
#
#   info: … Switchboard: routing …/inbox/msg/<id>
#         (type=…/Pando.Diagnostics.0.1.0/Issue-TOD) → Invoke-PandoDiagnosticsDateResult [Pando.Diagnostics]
#   info: … [PS Info] Invoke-PandoDiagnosticsDateResult: serverUtc=… from='<W6 DID>'
#
# Issue-TOD is terminal — W6 logs the result and sends no further reply.
#
# ---
#
# Step 8 — Send a second Query-TOD
#
# Repeat Step 5. The install line does NOT reappear (the package is cached in
# W6's lobes/), but the Import-Module lines do — JIT LOBEs reimport per dispatch.
#
# ---
#
# Step 9 — Reset between runs
#
# Stop both TDAs (Ctrl+C in Terminals A and B), then restart with --reset (each
# process deletes its own ~/.web7-pando/<name>-<hash8>/ directory on startup):

Write-Host "--- Step 9 — Reset between runs ---"
Start-TdaWindow -Title 'W5 [Wanderer]:8445' -WorkDir $bin -DotnetArgs '--name W5 --port 8445 --reset'
Start-TdaWindow -Title 'W6 [Wanderer]:8446' -WorkDir $bin -DotnetArgs '--name W6 --port 8446 --reset'

# Run this only to start over. Steps 10-14 continue with the SAME W5/W6 from
# Step 1 — skip Step 9 if you are continuing the walkthrough.
#
# ---
#
# Steps 10-14 — Register W5 with a Society (Wanderer → Citizen)
#
# W5 discovers Societies from the Federation and registers with one, becoming a
# Citizen. After Step 14, W5's identity.meta.json carries its parent Society DID
# and endpoint, and W5's local DID registry holds both the Citizen and Society
# DID Documents.
#
# Prerequisites: a Federation TDA and at least one Society TDA already running and
# bootstrapped — complete FEDERATIONDEBUG.ps1 §E.0-E.2 first. Two new terminals:

Write-Host "--- Steps 10-14 — Register W5 with a Society ---"

# Terminal D — Federation TDA on 8441
Start-TdaWindow -Title 'Federation:8441' -WorkDir $bin -DotnetArgs '--name Federation --port 8441'
# Terminal E — Society TDA on 8442
Start-TdaWindow -Title 'Society:8442'    -WorkDir $bin -DotnetArgs '--name Bindloss --port 8442'

# Complete E.0 (initialize-federation) and E.2 (register-society) from
# FEDERATIONDEBUG.ps1 before continuing. W5 (8445) must still be running from Step 1.
#
# ---
#
# Step 10 — Discover available Societies (Terminal C)

Write-Host "--- Step 10 — Discover available Societies ---"
$w5Common = Get-ChildItem $pando -Directory -Filter 'w5-*' | Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'lobes/Svrn7.Common.0.8.0/Svrn7.Common.0.8.0.psm1' }
Import-Module $w5Common -Force

$msg = @{
    typ  = 'application/didcomm-plain+json'
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = 'did:drn:svrn7.net/protocols/Svrn7.Federation.0.8.0/society-list'
    from = $w5Did
    to   = @('did:drn:federation.svrn7.net/federation/1.0/<genesis-hash>')   # informational only — routing is by @type
    body = '{}'
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8441 -Body $msg

# Terminal D → Invoke-Web7SocietyList; Terminal A (W5) → Invoke-Web7SocietyListResult
# stores the Society DID Document(s) locally. Note the Society endpoint from the
# result — used in Step 12.
#
# ---
#
# Step 11 — Generate Citizen key material (Terminal C)
#
# The Citizen DID is a distinct secp256k1-derived DID. Generate once and save.

Write-Host "--- Step 11 — Generate Citizen key material ---"
Import-Module (Join-Path $bin 'admin-tools/Svrn7.AdminTools/Svrn7.AdminTools.psm1')
$citizenKp  = New-Svrn7KeyPair
$citizenDid = New-Svrn7Did -KeyPair $citizenKp -Role 'Citizen' -SocietyName 'bindloss'
Write-Host "Citizen DID : $($citizenDid.Did)"
Write-Host "Public key  : $($citizenKp.PublicKeyHex)"
Write-Host "Private key : $($citizenKp.PrivateKeyHex)   <-- store securely"

# The Society re-derives the citizen DID server-side from publicKeyHex during
# register-citizen (identical formula), so the two match; the citizenDid field in
# Step 12's body is not actually read by the Society.
#
# ---
#
# Step 12 — Send register-citizen to the Society (Terminal C)

Write-Host "--- Step 12 — Send register-citizen ---"
$body = @{
    citizenDid         = $citizenDid.Did
    publicKeyHex       = $citizenKp.PublicKeyHex
    displayName        = 'W5'
    serviceEndpointUrl = 'http://localhost:8445/didcomm'   # W5's endpoint
} | ConvertTo-Json -Compress

$msg = @{
    typ  = 'application/didcomm-plain+json'
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = 'did:drn:svrn7.net/protocols/Svrn7.Onboarding.0.8.0/register-citizen'
    from = $citizenDid.Did
    to   = @('did:drn:federation.svrn7.net/bindloss/1.0/<genesis-hash>')   # informational only
    body = $body
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8442 -Body $msg

# Terminal E → Invoke-Web7RegisterCitizen; the Society delivers a receipt to W5.
#
# ---
#
# Step 13 — Verify W5 received the receipt (Terminal A)
#
#   info: … Switchboard: routing … (type=…/Svrn7.Onboarding.0.8.0/receipt)
#         → Invoke-Web7OnboardReceipt [Svrn7.Onboarding]
#   info: … [PS Info] Invoke-Web7OnboardReceipt: registered with
#         did:drn:federation.svrn7.net/bindloss/1.0/<hash> at http://localhost:8442/didcomm
#
# ---
#
# Step 14 — Verify persistence (Terminal C)
#
# Parent-tier wiring is written to W5's identity.meta.json by SetParentTda:

Write-Host "--- Step 14 — Verify identity.meta.json ---"
Get-ChildItem $pando -Directory -Filter 'w5-*' | Select-Object -First 1 |
    ForEach-Object { Get-Content (Join-Path $_.FullName 'identity.meta.json') -Raw | ConvertFrom-Json } |
    Select-Object did, parentTdaDid, parentTdaEndpointUrl

# Expected:
#   did                  parentTdaDid                                      parentTdaEndpointUrl
#   ---                  ------------                                      --------------------
#   did:drn:wanderer...  did:drn:federation.svrn7.net/bindloss/1.0/<hash>  http://localhost:8442/didcomm
#
# The Citizen + Society DID Documents are in W5's encrypted svrn7-dids.db — inspect with:
#
#   $env:PANDO_WALLET_PASSWORD = 'wanderer-debug'
#   dotnet "$bin\Svrn7.TDA.dll" db-shell --name W5 --db dids --collection Documents
#
# On the next restart W5 reads parentTdaDid / parentTdaEndpointUrl from
# identity.meta.json automatically — no appsettings.json entries needed.
#
# ---
#
# Troubleshooting
#
# | Symptom                                              | Cause                                                | Fix                                                                    |
# |------------------------------------------------------|------------------------------------------------------|------------------------------------------------------------------------ |
# | `TDA failed to start: … PANDO_WALLET_PASSWORD is not set` | env var missing and no interactive console       | `$env:PANDO_WALLET_PASSWORD = '…'` before launching                     |
# | `TDA failed to start: LOBE package 'Svrn7.Common' … is not in the LOBE library` | lobe-library/ empty            | Copy `dist\*.nupkg` to `~/.web7-pando/lobe-library/` (Prerequisites)    |
# | `wrong wallet password.`                             | password differs from the one the wallet was created with | Use the original, or `--reset` to re-bootstrap                     |
# | `Status: ConnectionRefused` posting to 8446          | W6 not running or still starting                     | Wait for W6's `KestrelListenerService: listening on port 8446` line     |
# | No `Issue-TOD` delivered                             | recipient DID Document not in the sender's registry  | Ensure the recipient bootstrapped before sending                        |
# | instance folder not found under `~/.web7-pando`      | TDA not started yet, or a different `--data-root`    | Start the TDA once; check for `<name>-<hash8>/`                          |
