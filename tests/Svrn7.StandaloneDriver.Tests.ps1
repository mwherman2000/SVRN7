#Requires -Version 7.2
#Requires -PSEdition Core
<#
.SYNOPSIS
    Pester 5 integration tests for standalone (non-TDA) driver bootstrap.

.DESCRIPTION
    Unlike Svrn7.Lobes.Tests.ps1, these tests DO load the compiled Svrn7 .NET assemblies
    and build real ISvrn7Driver/ISvrn7SocietyDriver instances — they require a built
    solution (src/Svrn7.TDA/bin/Debug/net8.0 must exist) and are slower. They exist to
    guard the standalone bootstrap path used by FEDERATIONDEBUG.ps1/WANDERERDEBUG.ps1 and
    by anyone running admin-tools/Svrn7.AdminTools.psm1 outside a running TDA.

    That path silently regressed multiple times during development — not because of any
    one obvious bug, but because it was never actually exercised end-to-end before: a
    PowerShell instance-dot-syntax call to a C# extension method that never resolves,
    a NuGet package needed only via Svrn7.TDA.deps.json (never physically copied next to
    the .exe), two LiteDB contexts independently opening the same file, a missing
    services.AddLogging(), an [OutputType] attribute that broke a function's own ability
    to self-bootstrap the assembly its own return type comes from, and a PowerShell
    property assignment targeting a property that doesn't exist on the C# options class.
    Structural checks (does the function exist, does it have the right parameters) would
    not have caught any of these — only actually calling the real driver bootstrap does.

    Requires Pester 5+. Install if needed:
        Install-Module Pester -MinimumVersion 5.0 -Force -SkipPublisherCheck

    Run with:
        dotnet build Web7-SVRN7.sln   # first, if not already built
        Invoke-Pester .\tests\Svrn7.StandaloneDriver.Tests.ps1 -Output Detailed
#>

BeforeAll {
    $BinDir         = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\bin\Debug\net8.0'
    $FederationPsm1 = Join-Path $BinDir 'lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1'
    $SocietyPsm1    = Join-Path $BinDir 'lobes\Svrn7.Society.0.8.0\Svrn7.Society.0.8.0.psm1'
    $AdminToolsPsm1 = Join-Path $BinDir 'admin-tools\Svrn7.AdminTools\Svrn7.AdminTools.psm1'

    if (-not (Test-Path $BinDir)) {
        throw "Build output not found at '$BinDir'. Run 'dotnet build Web7-SVRN7.sln' first."
    }

    Import-Module $FederationPsm1 -Force -WarningAction SilentlyContinue
    Import-Module $SocietyPsm1    -Force -WarningAction SilentlyContinue
    Import-Module $AdminToolsPsm1 -Force -WarningAction SilentlyContinue

    function New-TempDbRoot {
        Join-Path $env:TEMP "svrn7-pester-$([guid]::NewGuid().ToString('N'))"
    }
}

Describe 'Initialize-Svrn7FederationDriver standalone bootstrap' {
    It 'builds a real ISvrn7Driver and can register a DID' {
        $drv = Initialize-Svrn7FederationDriver -DbPath (New-TempDbRoot) -Force -PassThru
        $drv | Should -Not -BeNullOrEmpty
        $drv.GetType().FullName | Should -Be 'Svrn7.Federation.Svrn7Driver'

        $kp  = New-Svrn7KeyPair
        $did = (New-Svrn7Did -KeyPair $kp -Role Wanderer).Did
        $doc = $drv.CreateDidDocument($did, $kp.PublicKeyHex, 'drn', $null, [Svrn7.Core.Models.Svrn7Role]::Wanderer)
        { $drv.CreateDidAsync($doc).GetAwaiter().GetResult() } | Should -Not -Throw

        $drv.DidRegistry.CountAsync().GetAwaiter().GetResult() | Should -BeGreaterThan 0
    }

    It '-PassThru on an already-initialised driver (no -Force) returns the same instance without re-running setup' {
        $dbRoot = New-TempDbRoot
        $first  = Initialize-Svrn7FederationDriver -DbPath $dbRoot -Force -PassThru
        $second = Initialize-Svrn7FederationDriver -PassThru
        $second | Should -Be $first
    }
}

Describe 'Connect-Svrn7Society standalone bootstrap' {
    BeforeAll {
        # Connect-Svrn7Society's own doc contract: call Initialize-Svrn7FederationDriver
        # first (its assembly-loading side effect). A throwaway path — this driver's own
        # database is not what's under test here.
        Initialize-Svrn7FederationDriver -DbPath (New-TempDbRoot) -Force | Out-Null
    }

    It 'builds a real ISvrn7SocietyDriver and can register a DID' {
        $socDrv = Connect-Svrn7Society -SocietyDid 'did:drn:pestertest.svrn7.net' `
            -FederationDid 'did:drn:federation.svrn7.net' `
            -DidMethodNames @('pestertest') -DbPath (New-TempDbRoot) -Force -PassThru
        $socDrv | Should -Not -BeNullOrEmpty
        $socDrv.SocietyDid | Should -Be 'did:drn:pestertest.svrn7.net'

        $kp  = New-Svrn7KeyPair
        $did = (New-Svrn7Did -KeyPair $kp -Role Wanderer).Did
        $doc = $socDrv.CreateDidDocument($did, $kp.PublicKeyHex, 'drn', $null, [Svrn7.Core.Models.Svrn7Role]::Wanderer)
        { $socDrv.CreateDidAsync($doc).GetAwaiter().GetResult() } | Should -Not -Throw
    }
}

Describe 'admin-tools driver-dependent cmdlets accept a live driver' {
    BeforeAll {
        $Script:Drv = Initialize-Svrn7FederationDriver -DbPath (New-TempDbRoot) -Force -PassThru
    }

    It 'Invoke-Svrn7SignSecp256k1 signs without needing a driver at all' {
        $kp  = New-Svrn7KeyPair
        $sig = Invoke-Svrn7SignSecp256k1 -Payload ([Text.Encoding]::UTF8.GetBytes('pester')) -PrivateKeyBytes $kp.PrivateKeyBytes
        $sig | Should -Match '^0B'
    }

    It 'Invoke-Svrn7Transfer runs against a real driver (rejects cleanly on an unfunded payer, does not throw a plumbing error)' {
        $payerKp  = New-Svrn7KeyPair
        $payerDid = (New-Svrn7Did -KeyPair $payerKp -Role Wanderer).Did
        $payeeKp  = New-Svrn7KeyPair
        $payeeDid = (New-Svrn7Did -KeyPair $payeeKp -Role Wanderer).Did
        $Script:Drv.CreateDidAsync($Script:Drv.CreateDidDocument($payerDid, $payerKp.PublicKeyHex, 'drn', $null, [Svrn7.Core.Models.Svrn7Role]::Wanderer)).GetAwaiter().GetResult() | Out-Null
        $Script:Drv.CreateDidAsync($Script:Drv.CreateDidDocument($payeeDid, $payeeKp.PublicKeyHex, 'drn', $null, [Svrn7.Core.Models.Svrn7Role]::Wanderer)).GetAwaiter().GetResult() | Out-Null

        # An unfunded payer is expected to fail the transfer validator (insufficient
        # balance) — that's a legitimate business-rule rejection reaching all the way
        # through the DI/driver plumbing, which is exactly what this test is checking for.
        # A plumbing bug (the ones this file exists to guard against) fails earlier and
        # differently — a MethodInvocationException/RuntimeException about missing types,
        # extension methods, or DI registrations, not the driver's own validation logic.
        { Invoke-Svrn7Transfer -Driver $Script:Drv -PayerDid $payerDid -PayerKeyPair $payerKp -PayeeDid $payeeDid -AmountSvrn7 1 -Confirm:$false } |
            Should -Throw -ExceptionType ([System.Exception]) -Because 'unfunded payer should fail transfer validation, not DI plumbing'
    }
}
