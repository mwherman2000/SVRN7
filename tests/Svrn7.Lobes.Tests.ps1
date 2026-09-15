#Requires -Version 7.2
#Requires -PSEdition Core
<#
.SYNOPSIS
    Pester 5 tests for the Svrn7.Common, Svrn7.Federation, and Svrn7.Society LOBEs.

.DESCRIPTION
    These tests cover the PowerShell LOBE layer independently of the C# test suite.
    They deliberately avoid loading the Svrn7 .NET assemblies (no Initialize-Svrn7FederationDriver
    call), so they run without a compiled solution and without a live TDA.

    Requires Pester 5+. Install if needed:
        Install-Module Pester -MinimumVersion 5.0 -Force -SkipPublisherCheck

    Run with:
        Invoke-Pester .\tests\Svrn7.Lobes.Tests.ps1 -Output Detailed
#>

BeforeAll {
    $LobesDir      = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\lobes'
    $CommonPsm1    = Join-Path $LobesDir 'Svrn7.Common.0.8.0\Svrn7.Common.0.8.0.psm1'
    $FederationPsm1 = Join-Path $LobesDir 'Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1'
    $SocietyPsm1   = Join-Path $LobesDir 'Svrn7.Society.0.8.0\Svrn7.Society.0.8.0.psm1'

    # Load Common helpers directly into this script's scope via the scriptblock pattern.
    # This is the same mechanism Federation and Society use — avoids .psm1 extension scoping.
    . ([scriptblock]::Create([System.IO.File]::ReadAllText($CommonPsm1)))
}

# ── Send-LocalDIDCommMessage: exported by Federation ─────────────────────────

Describe 'Send-LocalDIDCommMessage exported by Svrn7.Federation' {
    BeforeAll {
        # Import without $SVRN7_LOBES_DIR so Common is loaded into Federation's scope.
        $LobesDir = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\lobes'
        Import-Module (Join-Path $LobesDir 'Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1') -Force -WarningAction SilentlyContinue
    }

    AfterAll {
        Remove-Module Svrn7.Federation -ErrorAction SilentlyContinue
    }

    It 'Send-LocalDIDCommMessage is callable after importing Svrn7.Federation' {
        Get-Command Send-LocalDIDCommMessage -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }

    It 'Send-LocalDIDCommMessage -Body is mandatory' {
        $cmd = Get-Command Send-LocalDIDCommMessage
        $bodyAttr = $cmd.Parameters['Body'].Attributes |
            Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } |
            Select-Object -First 1
        $bodyAttr.Mandatory | Should -BeTrue
    }

    It 'Send-LocalDIDCommMessage -Port has default 8443' {
        $cmd = Get-Command Send-LocalDIDCommMessage
        $scriptText = $cmd.ScriptBlock.ToString()
        $scriptText | Should -Match '8443'
    }
}

# ── Send-LocalDIDCommMessage: exported by Svrn7.Society ──────────────────────

Describe 'Send-LocalDIDCommMessage exported by Svrn7.Society' {
    BeforeAll {
        $LobesDir = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\lobes'
        # Society loads Common in standalone mode too — import without TDA context.
        Import-Module (Join-Path $LobesDir 'Svrn7.Society.0.8.0\Svrn7.Society.0.8.0.psm1') -Force -WarningAction SilentlyContinue
    }

    AfterAll {
        Remove-Module Svrn7.Society -ErrorAction SilentlyContinue
    }

    It 'Send-LocalDIDCommMessage is callable after importing Svrn7.Society' {
        Get-Command Send-LocalDIDCommMessage -ErrorAction SilentlyContinue | Should -Not -BeNullOrEmpty
    }
}

# ── Svrn7.AdminTools: private-key-bearing cmdlets isolated from eager LOBEs ───
#
# Regression guard for the LOBEGUIDE.md "Division of Responsibility" split: these
# cmdlets used to live inside Svrn7.Federation.0.8.0.psm1 / Svrn7.Society.0.8.0.psm1 —
# eager LOBEs, meaning every function they define is loaded into the shared
# InitialSessionState every dispatch runspace is built from, reachable by name from any
# handler's runspace regardless of which protocol triggered it. Moving them to
# admin-tools/Svrn7.AdminTools.psm1 (outside lobes/, no .lobe.json descriptor) makes
# "no inbound DIDComm message can reach these" structural instead of resting on
# "no registered handler happens to call this today." These tests fail loudly if
# anyone re-adds one of the moved names back into either eager module.

Describe 'Svrn7.AdminTools cmdlets are absent from the eager LOBE modules' {
    BeforeAll {
        $LobesDir = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\lobes'
        Import-Module (Join-Path $LobesDir 'Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1') -Force -WarningAction SilentlyContinue
        Import-Module (Join-Path $LobesDir 'Svrn7.Society.0.8.0\Svrn7.Society.0.8.0.psm1') -Force -WarningAction SilentlyContinue
    }

    AfterAll {
        Remove-Module Svrn7.Federation -ErrorAction SilentlyContinue
        Remove-Module Svrn7.Society -ErrorAction SilentlyContinue
    }

    It 'Svrn7.Federation no longer exports <_>' -ForEach @(
        'New-Svrn7KeyPair'
        'Invoke-Svrn7SignSecp256k1'
        'Invoke-Svrn7Transfer'
        'Invoke-Svrn7BatchTransfer'
    ) {
        Get-Command $_ -Module Svrn7.Federation -ErrorAction SilentlyContinue |
            Should -BeNullOrEmpty
    }

    It 'Svrn7.Society no longer exports <_>' -ForEach @(
        'Invoke-Svrn7ExternalTransfer'
        'Invoke-Svrn7FederationTransfer'
    ) {
        Get-Command $_ -Module Svrn7.Society -ErrorAction SilentlyContinue |
            Should -BeNullOrEmpty
    }
}

Describe 'Svrn7.AdminTools module' {
    BeforeAll {
        $AdminToolsPsm1 = Join-Path $PSScriptRoot '..\src\Svrn7.TDA\admin-tools\Svrn7.AdminTools\Svrn7.AdminTools.psm1'
        Import-Module $AdminToolsPsm1 -Force -WarningAction SilentlyContinue
    }

    AfterAll {
        Remove-Module Svrn7.AdminTools -ErrorAction SilentlyContinue
    }

    It 'exports <_>' -ForEach @(
        'New-Svrn7KeyPair'
        'Invoke-Svrn7SignSecp256k1'
        'Invoke-Svrn7Transfer'
        'Invoke-Svrn7BatchTransfer'
        'Invoke-Svrn7ExternalTransfer'
        'Invoke-Svrn7FederationTransfer'
    ) {
        Get-Command $_ -Module Svrn7.AdminTools -ErrorAction SilentlyContinue |
            Should -Not -BeNullOrEmpty
    }

    It 'Invoke-Svrn7Transfer takes an explicit -Driver parameter, not an ambient singleton' {
        # Untyped by design: a [Svrn7.Federation.ISvrn7Driver] type constraint here would
        # be resolved at module *parse* time, forcing Svrn7.AdminTools.psm1 to eagerly load
        # every Svrn7 assembly just to be imported — a hard dependency the original
        # Svrn7.Federation.0.8.0.psm1 never had, since it referenced the driver only via an
        # untyped $Script: singleton. Presence of the parameter is what this test checks.
        $cmd = Get-Command Invoke-Svrn7Transfer
        $cmd.Parameters.ContainsKey('Driver') | Should -BeTrue
    }

    It 'Invoke-Svrn7ExternalTransfer takes an explicit -SocietyDriver parameter' {
        $cmd = Get-Command Invoke-Svrn7ExternalTransfer
        $cmd.Parameters.ContainsKey('SocietyDriver') | Should -BeTrue
    }

    It 'Invoke-Svrn7FederationTransfer takes an explicit -SocietyDriver parameter' {
        $cmd = Get-Command Invoke-Svrn7FederationTransfer
        $cmd.Parameters.ContainsKey('SocietyDriver') | Should -BeTrue
    }
}

# ── Build-CanonicalTransferJson ───────────────────────────────────────────────
# Common.psm1 is dot-sourced directly in BeforeAll above.

Describe 'Build-CanonicalTransferJson' {
    It 'produces fields in normative order (§5.2)' {
        $json = Build-CanonicalTransferJson `
            -PayerDid    'did:drn:payer-test' `
            -PayeeDid    'did:drn:payee-test' `
            -AmountGrana 500 `
            -Nonce       'abc123' `
            -Timestamp   '2025-01-01T00:00:00Z' `
            -Memo        $null

        $doc   = [System.Text.Json.JsonDocument]::Parse($json)
        $names = $doc.RootElement.EnumerateObject() | ForEach-Object { $_.Name }
        $names | Should -Be @('PayerDid', 'PayeeDid', 'AmountGrana', 'Nonce', 'Timestamp', 'Memo')
    }

    It 'serializes a non-null Memo correctly' {
        $json = Build-CanonicalTransferJson `
            -PayerDid    'did:drn:p-test' `
            -PayeeDid    'did:drn:q-test' `
            -AmountGrana 1 `
            -Nonce       'n' `
            -Timestamp   't' `
            -Memo        'payment for invoice 42'

        $doc  = [System.Text.Json.JsonDocument]::Parse($json)
        $memo = $doc.RootElement.GetProperty('Memo').GetString()
        $memo | Should -Be 'payment for invoice 42'
    }

    It 'serializes a null Memo as JSON null (not absent)' {
        $json = Build-CanonicalTransferJson `
            -PayerDid    'did:drn:p-test' `
            -PayeeDid    'did:drn:q-test' `
            -AmountGrana 1 `
            -Nonce       'n' `
            -Timestamp   't' `
            -Memo        $null

        $doc      = [System.Text.Json.JsonDocument]::Parse($json)
        $memoKind = $doc.RootElement.GetProperty('Memo').ValueKind
        $memoKind | Should -Be ([System.Text.Json.JsonValueKind]::Null)
    }

    It 'produces compact JSON with no line breaks' {
        $json = Build-CanonicalTransferJson `
            -PayerDid    'did:drn:p-test' `
            -PayeeDid    'did:drn:q-test' `
            -AmountGrana 1 `
            -Nonce       'n' `
            -Timestamp   't' `
            -Memo        $null

        $json | Should -Not -Match "`n"
        $json | Should -Not -Match "`r"
    }

    It 'encodes AmountGrana as a JSON number, not a string' {
        $json = Build-CanonicalTransferJson `
            -PayerDid    'did:drn:p-test' `
            -PayeeDid    'did:drn:q-test' `
            -AmountGrana 12345 `
            -Nonce       'n' `
            -Timestamp   't' `
            -Memo        $null

        $doc    = [System.Text.Json.JsonDocument]::Parse($json)
        $kind   = $doc.RootElement.GetProperty('AmountGrana').ValueKind
        $value  = $doc.RootElement.GetProperty('AmountGrana').GetInt64()
        $kind   | Should -Be ([System.Text.Json.JsonValueKind]::Number)
        $value  | Should -Be 12345
    }
}

# ── Initialize-Svrn7Assemblies path resolution ────────────────────────────────

Describe 'Initialize-Svrn7Assemblies path resolution' {
    BeforeAll {
        # Remove any SVRN7_BIN_PATH override so the fallback logic runs.
        $Saved = $env:SVRN7_BIN_PATH
        Remove-Item Env:\SVRN7_BIN_PATH -ErrorAction SilentlyContinue
    }

    AfterAll {
        if ($null -ne $Saved) { $env:SVRN7_BIN_PATH = $Saved }
    }

    It 'throws DirectoryNotFoundException when $env:SVRN7_BIN_PATH points to nonexistent path' {
        $env:SVRN7_BIN_PATH = 'C:\does\not\exist\svrn7'
        { Initialize-Svrn7Assemblies -ModuleRoot (Get-Location).Path } |
            Should -Throw -ExceptionType ([System.IO.DirectoryNotFoundException])
        Remove-Item Env:\SVRN7_BIN_PATH -ErrorAction SilentlyContinue
    }

    It 'falls back to two-levels-up when ModuleRoot has no bin/ subfolder' {
        # Pass a completely nonexistent module root — no directories are created.
        # The function computes binPath as GetFullPath('ModuleRoot/../..').
        # Because neither bin/ nor the two-levels-up path exist, it throws
        # DirectoryNotFoundException naming the fallback (not the bin/) path.
        $moduleDir        = Join-Path $env:TEMP "svrn7-ne-$([System.Guid]::NewGuid().ToString('N'))\lobes\Svrn7.Module"
        $expectedFallback = [System.IO.Path]::GetFullPath((Join-Path $moduleDir '../..'))

        { Initialize-Svrn7Assemblies -ModuleRoot $moduleDir } |
            Should -Throw -ExceptionType ([System.IO.DirectoryNotFoundException])

        try { Initialize-Svrn7Assemblies -ModuleRoot $moduleDir } catch {
            $_.Exception.Message | Should -Match ([regex]::Escape($expectedFallback))
        }
    }

    It 'uses $env:SVRN7_BIN_PATH when set, even if the path does not exist' {
        $env:SVRN7_BIN_PATH = 'C:\svrn7-imaginary-bin-path-does-not-exist'
        try {
            { Initialize-Svrn7Assemblies -ModuleRoot (Get-Location).Path } |
                Should -Throw -ExceptionType ([System.IO.DirectoryNotFoundException])
            try { Initialize-Svrn7Assemblies -ModuleRoot (Get-Location).Path } catch {
                $_.Exception.Message | Should -Match ([regex]::Escape('C:\svrn7-imaginary-bin-path-does-not-exist'))
            }
        } finally {
            Remove-Item Env:\SVRN7_BIN_PATH -ErrorAction SilentlyContinue
        }
    }
}
