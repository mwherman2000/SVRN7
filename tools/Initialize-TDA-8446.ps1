#Requires -Version 7.2
<#
.SYNOPSIS
    Start W6 on port 8446 and send it a Pando.Diagnostics Query-TOD (self-send).

.DESCRIPTION
    Per-identity runtime storage (docs/AGENTWALLET.md). W6 unlocks/creates an
    encrypted wallet with $env:PANDO_WALLET_PASSWORD and stores everything under
    ~/.web7-pando/w6-<genesisHash8>/. Its DID is read from that folder's
    cleartext identity.meta.json (no more <port>/mem/agent-identity.json).

    Pando.Diagnostics is a JIT LOBE — it is not in the eager list, so W6 installs
    it from ~/.web7-pando/lobe-library/ the first time the Query-TOD message is
    dispatched.

    Run this from a PowerShell 7 terminal at the repo root.
#>
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $Password = 'svrn7-testnet',
    [switch] $Reset,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$proj    = Join-Path $RepoRoot 'src/Svrn7.TDA/Svrn7.TDA.csproj'
$binDir  = Join-Path $RepoRoot 'src/Svrn7.TDA/bin/Debug/net8.0'
$dll     = Join-Path $binDir 'Svrn7.TDA.dll'
$distDir = Join-Path $RepoRoot 'dist'
$pando   = Join-Path $HOME '.web7-pando'
$lobeLib = Join-Path $pando 'lobe-library'

# ── Step 1 — build + fill the LOBE library, then start W6 ────────────────────
if (-not $SkipBuild) { dotnet build $proj -c Debug --nologo -v q }
New-Item -ItemType Directory -Force -Path $lobeLib | Out-Null
Copy-Item (Join-Path $distDir '*.nupkg') -Destination $lobeLib -Force

$env:PANDO_WALLET_PASSWORD = $Password
$reset = if ($Reset) { ' --reset' } else { '' }

Write-Host "--- Step 1 — start W6 on :8446 ---"
Start-Process cmd.exe -ArgumentList "/k title W6 [Wanderer] :8446 && dotnet `"$dll`" --name W6 --port 8446$reset" -WorkingDirectory $binDir
Write-Host "Wait for W6's 'listening on port 8446' banner line, then press Enter."
Pause

# ── Step 2 — read W6's DID from its instance folder ──────────────────────────
Write-Host "--- Step 2 — read W6's DID ---"
$w6Meta = Get-ChildItem $pando -Directory -Filter 'w6-*' |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'identity.meta.json' }
if (-not (Test-Path $w6Meta)) { throw "W6 instance folder not found under $pando." }
$w6Did = (Get-Content $w6Meta -Raw | ConvertFrom-Json).did
Write-Host "W6 DID: $w6Did"

# ── Step 3 — import the send helper ─────────────────────────────────────────
# Send-LocalDIDCommMessage ships in Svrn7.Common; it is an eager LOBE, so W6
# has already installed it under its instance lobes/ folder.
$common = Get-ChildItem $pando -Directory -Filter 'w6-*' |
    Select-Object -First 1 |
    ForEach-Object { Join-Path $_.FullName 'lobes/Svrn7.Common.0.8.0/Svrn7.Common.0.8.0.psm1' }
Import-Module $common -Force

# ── Step 4 — self-send Query-TOD (triggers JIT install of Pando.Diagnostics) ─
Write-Host "--- Step 4 — send Query-TOD from W6 to itself ---"
$msg = @{
    typ  = 'application/didcomm-plain+json'
    id   = "did:drn:svrn7.net/didcomm/msg/$([System.Guid]::NewGuid().ToString('N'))"
    type = 'did:drn:svrn7.net/protocols/Pando.Diagnostics.0.1.0/Query-TOD'
    from = $w6Did
    to   = @($w6Did)
    body = '{}'
} | ConvertTo-Json

Send-LocalDIDCommMessage -Port 8446 -Body $msg

Write-Host ""
Write-Host "Watch W6's window: 'LobeInstaller: installing Pando.Diagnostics 0.1.0' then an Issue-TOD reply."
Write-Host "Inspect its inbox:  dotnet `"$dll`" db-shell --name W6 --db msg --collection InboundMessages"
