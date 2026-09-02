#Requires -Version 7.2
<#
.SYNOPSIS
    Starts a local SVRN7 testnet — four Wanderer TDA instances.

.DESCRIPTION
    Launches four TDA processes, each in its own titled console window:

        Wanderer1   --port 8441
        Wanderer2   --port 8442
        Wanderer3   --port 8443
        Wanderer4   --port 8444

    Per-identity runtime storage (docs/AGENTWALLET.md). Each TDA:
      * unlocks (or, on first run, creates) an encrypted wallet using the
        shared testnet password in $env:PANDO_WALLET_PASSWORD;
      * stores its wallet + encrypted databases + per-instance lobes/ under
        ~/.web7-pando/<name>-<genesisHash8>/;
      * installs its eager LOBEs from ~/.web7-pando/lobe-library/ (this script
        fills that from dist/ before launching).

    On first run each node auto-generates a Wanderer identity and binds the
    given --port verbatim. On later runs it rebinds the published port.

    Close each console window (or Ctrl+C in it) to stop that node.

.PARAMETER RepoRoot
    Repo root. Default: the parent of this script's tools/ folder.

.PARAMETER Password
    Shared wallet password for every testnet node. Default: "svrn7-testnet".

.PARAMETER Reset
    Pass --reset to every node (wipes each identity's runtime directory and
    re-bootstraps). Prompts per node on a terminal.

.PARAMETER SkipBuild
    Don't run `dotnet build` first (assume the DLL and dist/*.nupkg are current).
#>
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string] $Password = 'svrn7-testnet',
    [switch] $Reset,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

$proj      = Join-Path $RepoRoot 'src/Svrn7.TDA/Svrn7.TDA.csproj'
$binDir    = Join-Path $RepoRoot 'src/Svrn7.TDA/bin/Debug/net8.0'
$dll       = Join-Path $binDir 'Svrn7.TDA.dll'
$distDir   = Join-Path $RepoRoot 'dist'
$pandoHome = Join-Path $HOME '.web7-pando'
$lobeLib   = Join-Path $pandoHome 'lobe-library'

# 1. Build (also (re)generates dist/*.nupkg via the BuildLOBEPackages target).
if (-not $SkipBuild) {
    Write-Host "Building Svrn7.TDA ..." -ForegroundColor Cyan
    dotnet build $proj -c Debug --nologo -v q
}
if (-not (Test-Path $dll)) { throw "Svrn7.TDA.dll not found at '$dll'. Build first (omit -SkipBuild)." }

# 2. Populate the machine-level LOBE library from dist/ (Publish does this too;
#    here we just copy so the testnet needs no publish step).
if (-not (Test-Path $distDir)) { throw "No dist/ — the LOBE packages were not built. Run without -SkipBuild." }
New-Item -ItemType Directory -Force -Path $lobeLib | Out-Null
Copy-Item (Join-Path $distDir '*.nupkg') -Destination $lobeLib -Force
Write-Host "LOBE library: $((Get-ChildItem $lobeLib -Filter *.nupkg).Count) package(s) in $lobeLib" -ForegroundColor Cyan

# 3. Shared wallet password for every node (inherited by the child processes).
$env:PANDO_WALLET_PASSWORD = $Password
Write-Host "Wallet password for all nodes: '$Password' (env PANDO_WALLET_PASSWORD)" -ForegroundColor Yellow

$nodes = @(
    @{ Name = 'Wanderer1'; Port = 8441 }
    @{ Name = 'Wanderer2'; Port = 8442 }
    @{ Name = 'Wanderer3'; Port = 8443 }
    @{ Name = 'Wanderer4'; Port = 8444 }
)
$resetArg = if ($Reset) { ' --reset' } else { '' }

foreach ($node in $nodes) {
    $inner = "dotnet `"$dll`" --name $($node.Name) --port $($node.Port)$resetArg"
    Write-Host "Launching: $inner"
    if ($IsWindows) {
        $args = "/k title $($node.Name) [Wanderer] :$($node.Port) && $inner"
        $proc = Start-Process cmd.exe -ArgumentList $args -WorkingDirectory $binDir -PassThru
    }
    else {
        $proc = Start-Process dotnet -ArgumentList "`"$dll`" --name $($node.Name) --port $($node.Port)$resetArg" `
                    -WorkingDirectory $binDir -PassThru
    }
    Write-Host "Started   $($node.Name)  role=Wanderer  port=$($node.Port)  pid=$($proc.Id)  " `
               "→ $pandoHome\$($node.Name.ToLower())-<hash8>\"
}

Write-Host ""
Write-Host "Inspect a node's encrypted databases:" -ForegroundColor Cyan
Write-Host "  `$env:PANDO_WALLET_PASSWORD='$Password'; dotnet `"$dll`" db-shell --name Wanderer1"
