#Requires -Version 7.2
<#
.SYNOPSIS
    Starts a single Wanderer TDA (Wanderer3) on port 8443.

.DESCRIPTION
    Per-identity runtime storage (docs/AGENTWALLET.md): the node unlocks (or, on
    first run, creates) an encrypted wallet with $env:PANDO_WALLET_PASSWORD and
    stores everything under ~/.web7-pando/wanderer3-<genesisHash8>/. Eager LOBEs
    install from ~/.web7-pando/lobe-library/, which this script fills from dist/.

.PARAMETER RepoRoot   Repo root. Default: parent of tools/.
.PARAMETER Password   Wallet password. Default: "svrn7-testnet".
.PARAMETER Reset      Pass --reset (wipe + re-bootstrap this identity).
.PARAMETER FederationDomain
    Passed as --federationdomain for drn.directory endpoint discovery. Default: "svrn7.net".
.PARAMETER SkipBuild  Skip `dotnet build`.
#>
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $Password = 'svrn7-testnet',
    [switch] $Reset,
    [string] $FederationDomain = 'svrn7.net',
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$proj    = Join-Path $RepoRoot 'src/Svrn7.TDA/Svrn7.TDA.csproj'
$binDir  = Join-Path $RepoRoot 'src/Svrn7.TDA/bin/Debug/net8.0'
$dll     = Join-Path $binDir 'Svrn7.TDA.dll'
$distDir = Join-Path $RepoRoot 'dist'
$lobeLib = Join-Path (Join-Path $HOME '.web7-pando') 'lobe-library'

if (-not $SkipBuild) {
    Write-Host "Building Svrn7.TDA ..." -ForegroundColor Cyan
    dotnet build $proj -c Debug --nologo -v q
}
if (-not (Test-Path $dll)) { throw "Svrn7.TDA.dll not found at '$dll'." }

New-Item -ItemType Directory -Force -Path $lobeLib | Out-Null
Copy-Item (Join-Path $distDir '*.nupkg') -Destination $lobeLib -Force
Write-Host "LOBE library: $((Get-ChildItem $lobeLib -Filter *.nupkg).Count) package(s)" -ForegroundColor Cyan

$env:PANDO_WALLET_PASSWORD = $Password

$reset = if ($Reset) { ' --reset' } else { '' }
$fed   = if ($FederationDomain) { " --federationdomain $FederationDomain" } else { '' }
$inner = "dotnet `"$dll`" --name Wanderer3 --port 8443$reset$fed"

Write-Host "Launching: $inner   (password '$Password')"
if ($IsWindows) {
    Start-Process cmd.exe -ArgumentList "/k title Wanderer3 [Wanderer] :8443 && $inner" -WorkingDirectory $binDir
}
else {
    Start-Process dotnet -ArgumentList "`"$dll`" --name Wanderer3 --port 8443$reset$fed" -WorkingDirectory $binDir
}
