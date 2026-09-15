#Requires -Version 7.2
#Requires -PSEdition Core
<#
.SYNOPSIS
    Svrn7 Admin Tools — interactive cmdlets that handle citizen/payer private key
    material directly.

.DESCRIPTION
    Deliberately NOT a LOBE: this file lives outside src/Svrn7.TDA/lobes/ and has no
    .lobe.json descriptor, so LobeManager never discovers it, never eager-loads it into
    the shared InitialSessionState the Switchboard builds dispatch runspaces from, and
    never JIT-imports it for a message dispatch. No inbound DIDComm message can ever
    reach a cmdlet in this file — that is structural (the file is outside LobeManager's
    scan directory), not merely "no registered handler currently calls it."

    Split out of Svrn7.Federation.0.8.0.psm1 / Svrn7.Society.0.8.0.psm1, where these
    cmdlets previously sat alongside registered protocol entrypoints in the same
    eager-loaded module — reachable by name from any dispatch runspace (PowerShell has
    no per-function ACL within a session) if a future registered handler ever contained
    a dynamic-invocation bug (Invoke-Expression / `&` on message-derived content). No
    such bug exists today, but the old arrangement made the "LOBEs never touch private
    key material" guarantee rest on that absence rather than on the architecture itself.

    None of these cmdlets need $SVRN7 or a TDA runspace — they are for a human running
    them directly from a standalone PowerShell session (the same way New-Svrn7Did or
    Send-LocalDIDCommMessage are used interactively in WANDERERDEBUG.ps1 /
    FEDERATIONDEBUG.ps1), supplying a keypair they already generated and hold. They take
    an ISvrn7Driver explicitly via -Driver / -SocietyDriver rather than reaching for an
    ambient singleton, so importing this module never implicitly depends on
    Svrn7.Federation.0.8.0.psm1's or Svrn7.Society.0.8.0.psm1's private module-scoped
    state (which lives in a separate, unreachable script scope from this file anyway).

    SETUP (standalone session)
        1. Import-Module .\lobes\Svrn7.Federation.0.8.0\Svrn7.Federation.0.8.0.psm1
        2. Import-Module .\admin-tools\Svrn7.AdminTools\Svrn7.AdminTools.psm1
        3. $drv = Initialize-Svrn7FederationDriver -PassThru
        4. $kp  = New-Svrn7KeyPair
        5. Invoke-Svrn7Transfer -Driver $drv -PayerDid ... -PayerKeyPair $kp ...

    For a Society-scoped transfer (Invoke-Svrn7ExternalTransfer /
    Invoke-Svrn7FederationTransfer), also import Svrn7.Society.0.8.0.psm1 and use
    Connect-Svrn7Society -PassThru instead, passing its output as -SocietyDriver.

.NOTES
    Never reference this file from lobes.config.json's eager list or any .lobe.json
    descriptor, and never move it under src/Svrn7.TDA/lobes/ — either would put it back
    in LobeManager's discovery scope and defeat the point of the split.
#>

# Same reasoning as Svrn7.Federation.0.8.0.psm1's own header: PowerShell 7 applies
# module-context scoping to a dot-sourced .psm1, which would hide these functions from
# this module's own scope. Invoking a scriptblock is always plain-script execution.
# This module is never eager/JIT-loaded by LobeManager, so $SVRN7_LOBES_DIR is never
# set when it's imported — the dot-source below always runs.
$_commonPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'lobes\Svrn7.Common.0.8.0\Svrn7.Common.0.8.0.psm1'
if (Test-Path -LiteralPath $_commonPath) {
    . ([scriptblock]::Create([System.IO.File]::ReadAllText($_commonPath)))
} else {
    Write-Warning "Svrn7.AdminTools: Svrn7.Common.0.8.0.psm1 not found at '$_commonPath' — cmdlets will fail."
}
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

###############################################################################
#region CRYPTOGRAPHY
###############################################################################

function New-Svrn7KeyPair {
<#
.SYNOPSIS
    Generates a secp256k1 key pair for signing SVRN7 transfer requests.
.DESCRIPTION
    Pure crypto — no driver or database connection required. The returned object can
    be piped into New-Svrn7Did (Svrn7.Federation.0.8.0.psm1) or passed to
    Invoke-Svrn7Transfer / Invoke-Svrn7ExternalTransfer / Invoke-Svrn7FederationTransfer
    in this module. Handle PrivateKeyBytes with care.
.EXAMPLE
    $kp = New-Svrn7KeyPair
    $kp.PublicKeyHex
.EXAMPLE
    New-Svrn7KeyPair | New-Svrn7Did -Role Wanderer
.OUTPUTS
    [PSCustomObject] Svrn7.KeyPair
        PublicKeyHex    [string]   33-byte compressed secp256k1 public key (hex).
        PrivateKeyBytes [byte[]]   32-byte raw private key.
        PrivateKeyHex   [string]   Hex of the private key.
        Algorithm       [string]   'Secp256k1'.
#>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()
    Initialize-Svrn7Assemblies -ModuleRoot $PSScriptRoot
    $kp = [Svrn7.Crypto.CryptoService]::new().GenerateSecp256k1KeyPair()
    [PSCustomObject]@{
        PSTypeName      = $Script:TypeKeyPair
        PublicKeyHex    = $kp.PublicKeyHex
        PrivateKeyBytes = $kp.PrivateKeyBytes
        PrivateKeyHex   = [System.Convert]::ToHexString($kp.PrivateKeyBytes).ToLower()
        Algorithm       = 'Secp256k1'
    }
}

function Invoke-Svrn7SignSecp256k1 {
<#
.SYNOPSIS
    Signs a byte payload with a secp256k1 private key (CESR-encoded output).
.DESCRIPTION
    Pure crypto — no driver or database connection required. Returns a CESR compact
    signature ('0B' + base64url-nopad). Invoke-Svrn7Transfer calls the same operation
    internally; use this directly for governance operation signing.
.PARAMETER Payload
    Raw bytes to sign.
.PARAMETER PrivateKeyBytes
    32-byte secp256k1 private key from New-Svrn7KeyPair.PrivateKeyBytes.
.EXAMPLE
    $sig = Invoke-Svrn7SignSecp256k1 `
               -Payload         ([Text.Encoding]::UTF8.GetBytes('hello')) `
               -PrivateKeyBytes $kp.PrivateKeyBytes
.OUTPUTS
    [string]  CESR-encoded secp256k1 signature.
#>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [byte[]] $Payload,
        [Parameter(Mandatory)] [byte[]] $PrivateKeyBytes
    )
    Initialize-Svrn7Assemblies -ModuleRoot $PSScriptRoot
    [Svrn7.Crypto.CryptoService]::new().SignSecp256k1($Payload, $PrivateKeyBytes)
}
#endregion

###############################################################################
#region TRANSFERS
###############################################################################

function Invoke-Svrn7Transfer {
<#
.SYNOPSIS
    Signs and submits a SVRN7 transfer request.
.DESCRIPTION
    Builds the canonical JSON per draft-herman-svrn7-monetary-protocol-00 §5.2:
        { PayerDid, PayeeDid, AmountGrana, Nonce, Timestamp, Memo }
    Signs the UTF-8 bytes with the payer's secp256k1 key (CESR '0B'), then calls
    ISvrn7Driver.TransferAsync() on the supplied driver. Field order is enforced
    automatically. A UUID nonce is generated if -Nonce is omitted.
.PARAMETER Driver
    An initialised ISvrn7Driver — e.g. from Initialize-Svrn7FederationDriver -PassThru
    (Svrn7.Federation.0.8.0.psm1).
.PARAMETER PayerDid
    DID of the payer. Must be Active.
.PARAMETER PayerKeyPair
    secp256k1 [Svrn7.KeyPair] for the payer.
.PARAMETER PayeeDid
    DID of the payee. Must be Active and permitted by the current Epoch.
.PARAMETER AmountSvrn7
    Amount in SVRN7. Mutually exclusive with -AmountGrana.
.PARAMETER AmountGrana
    Amount in grana. Mutually exclusive with -AmountSvrn7.
.PARAMETER Memo
    Optional memo (max 256 characters).
.PARAMETER Nonce
    Optional idempotency nonce. Auto-generated UUID if omitted.
.EXAMPLE
    Invoke-Svrn7Transfer -Driver $drv `
        -PayerDid $citizenDid -PayerKeyPair $kp `
        -PayeeDid $societyDid -AmountSvrn7 100
.OUTPUTS
    [PSCustomObject] Svrn7.TransferResult
        TransferId [string]; PayerDid [string]; PayeeDid [string]
        AmountGrana [long]; AmountSvrn7 [decimal]; Nonce [string]
        Timestamp [string]; Memo [string]; Success [bool]
.NOTES
    ISvrn7Driver method: TransferAsync(TransferRequest)
    Spec: draft-herman-svrn7-monetary-protocol-00 §§5-6
#>
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName='BySvrn7')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)] $Driver,
        [Parameter(Mandatory)] [string]        $PayerDid,
        [Parameter(Mandatory)] [PSCustomObject] $PayerKeyPair,
        [Parameter(Mandatory)] [string]        $PayeeDid,
        [Parameter(Mandatory, ParameterSetName='BySvrn7')]
        [ValidateRange(0.000001,1e15)] [double] $AmountSvrn7,
        [Parameter(Mandatory, ParameterSetName='ByGrana')]
        [ValidateRange(1L,[long]::MaxValue)] [long] $AmountGrana,
        [Parameter()] [ValidateLength(0,256)] [string] $Memo  = '',
        [Parameter()]                         [string] $Nonce = ''
    )
    $grana = if ($PSCmdlet.ParameterSetName -eq 'BySvrn7') { [long][Math]::Round($AmountSvrn7 * 1000000) } else { $AmountGrana }
    $svrn7 = [decimal]$grana / 1000000
    $nonce = if ($Nonce) { $Nonce } else { [Guid]::NewGuid().ToString('N') }
    $ts    = [DateTimeOffset]::UtcNow.ToString('O')
    $memo  = if ($Memo) { $Memo } else { $null }
    $json  = Build-CanonicalTransferJson $PayerDid $PayeeDid $grana $nonce $ts $memo
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $sig   = $Driver.SignSecp256k1($bytes, $PayerKeyPair.PrivateKeyBytes)
    Write-Verbose "Canonical: $json"
    if ($PSCmdlet.ShouldProcess($PayerDid, "Transfer $svrn7 SVRN7 to $PayeeDid")) {
        $r = $Driver.TransferAsync([Svrn7.Core.Models.TransferRequest]@{
            PayerDid=$PayerDid; PayeeDid=$PayeeDid; AmountGrana=$grana; Nonce=$nonce
            Timestamp=[DateTimeOffset]::Parse($ts); Signature=$sig; Memo=$memo
        }).GetAwaiter().GetResult()
        Resolve-OperationResult $r 'Transfer' | Out-Null
        $txId = $Driver.Blake3HexAsync($bytes).GetAwaiter().GetResult()
        [PSCustomObject]@{
            PSTypeName=$Script:TypeTransfer; TransferId=$txId
            PayerDid=$PayerDid; PayeeDid=$PayeeDid
            AmountGrana=$grana; AmountSvrn7=$svrn7; Nonce=$nonce
            Timestamp=$ts; Memo=$Memo; Success=$true
        }
    }
}

function Invoke-Svrn7BatchTransfer {
<#
.SYNOPSIS
    Signs and submits multiple transfer requests in one batch call.
.DESCRIPTION
    Accepts an array of transfer descriptors (each with PayerDid, PayerKeyPair,
    PayeeDid, AmountGrana; optional Memo, Nonce), signs each canonically, and
    calls ISvrn7Driver.BatchTransferAsync() on the supplied driver. Returns one
    result per input.
.PARAMETER Driver
    An initialised ISvrn7Driver — e.g. from Initialize-Svrn7FederationDriver -PassThru
    (Svrn7.Federation.0.8.0.psm1).
.PARAMETER Transfers
    Array of hashtables or PSCustomObjects with keys:
        PayerDid [string] Required; PayerKeyPair [Svrn7.KeyPair] Required
        PayeeDid [string] Required; AmountGrana [long] Required
        Memo [string] Optional; Nonce [string] Optional
.EXAMPLE
    $batch = @(
        @{ PayerDid=$d1; PayerKeyPair=$kp; PayeeDid=$d2; AmountGrana=100000000L },
        @{ PayerDid=$d1; PayerKeyPair=$kp; PayeeDid=$d3; AmountGrana=50000000L  }
    )
    Invoke-Svrn7BatchTransfer -Driver $drv -Transfers $batch
.OUTPUTS
    [PSCustomObject[]] Svrn7.BatchTransferResult — one per input.
.NOTES
    ISvrn7Driver method: BatchTransferAsync(IEnumerable<TransferRequest>)
#>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([PSCustomObject[]])]
    param(
        [Parameter(Mandatory)] $Driver,
        [Parameter(Mandatory, ValueFromPipeline)] [object[]] $Transfers
    )
    process {
        $reqs = [System.Collections.Generic.List[Svrn7.Core.Models.TransferRequest]]::new()
        $meta = [System.Collections.Generic.List[hashtable]]::new()
        foreach ($t in $Transfers) {
            $n    = if ($t.Nonce) { $t.Nonce } else { [Guid]::NewGuid().ToString('N') }
            $ts   = [DateTimeOffset]::UtcNow.ToString('O')
            $m    = if ($t.Memo) { [string]$t.Memo } else { $null }
            $g    = [long]$t.AmountGrana
            $json = Build-CanonicalTransferJson $t.PayerDid $t.PayeeDid $g $n $ts $m
            $b    = [System.Text.Encoding]::UTF8.GetBytes($json)
            $sig  = $Driver.SignSecp256k1($b, $t.PayerKeyPair.PrivateKeyBytes)
            $reqs.Add([Svrn7.Core.Models.TransferRequest]@{
                PayerDid=$t.PayerDid; PayeeDid=$t.PayeeDid; AmountGrana=$g
                Nonce=$n; Timestamp=[DateTimeOffset]::Parse($ts); Signature=$sig; Memo=$m })
            $meta.Add(@{ P=$t.PayerDid; Q=$t.PayeeDid; G=$g })
        }
        if ($PSCmdlet.ShouldProcess("$($reqs.Count) transfers", 'BatchTransfer')) {
            $results = $Driver.BatchTransferAsync($reqs).GetAwaiter().GetResult()
            $i = 0
            foreach ($r in $results) {
                $mm = $meta[$i++]
                [PSCustomObject]@{
                    PSTypeName=$Script:TypeBatchItem; PayerDid=$mm.P; PayeeDid=$mm.Q
                    AmountGrana=$mm.G; Success=$r.Success; ErrorMessage=$r.ErrorMessage
                }
            }
        }
    }
}

function Invoke-Svrn7ExternalTransfer {
<#
.SYNOPSIS
    Initiates a cross-Society Epoch 1 transfer from a citizen in this Society
    to a citizen in another Society via DIDComm SignThenEncrypt.
.DESCRIPTION
    Wraps ISvrn7SocietyDriver.TransferToExternalCitizenAsync() on the supplied driver.
    Validates through the nine-step Society transfer validator (which adds Society
    membership verification as Step 8 to the standard eight-step pipeline), debits the
    payer UTXO, issues a TransferOrderCredential VC, appends a CrossSocietyTransferDebit
    Merkle log entry, and dispatches the credential via DIDComm SignThenEncrypt to the
    target Society.

    The transfer is fire-and-forget: the originating Society debits immediately and the
    receipt arrives asynchronously via the DIDComm inbox processing service. Idempotency
    is guaranteed by TransferId (Blake3 hex of the canonical JSON): if the DIDComm
    delivery is retried, the receiving Society returns the cached receipt without
    double-crediting the payee.

    Requires the ecosystem to be in Epoch 1 or higher. In Epoch 0 use
    Invoke-Svrn7FederationTransfer.
.PARAMETER SocietyDriver
    An initialised ISvrn7SocietyDriver — e.g. from Connect-Svrn7Society -PassThru
    (Svrn7.Society.0.8.0.psm1).
.PARAMETER PayerDid
    DID of the payer. Must be an active citizen of this Society.
.PARAMETER PayerKeyPair
    Svrn7.KeyPair (secp256k1) for the payer, used to sign the canonical JSON.
.PARAMETER PayeeDid
    DID of the payee. Must be an active citizen of the target Society.
.PARAMETER TargetSocietyDid
    DID of the Society where the payee is registered.
.PARAMETER AmountSvrn7
    Transfer amount in SVRN7. Mutually exclusive with -AmountGrana.
.PARAMETER AmountGrana
    Transfer amount in grana. Mutually exclusive with -AmountSvrn7.
.PARAMETER Memo
    Optional memo, maximum 256 characters.
.PARAMETER Nonce
    Optional idempotency nonce. A UUID is auto-generated if omitted.
.OUTPUTS
    PSCustomObject [Svrn7.ExternalTransferResult]
        TransferId [string]; PayerDid [string]; PayeeDid [string]
        TargetSocietyDid [string]; AmountGrana [long]; AmountSvrn7 [decimal]
        Nonce [string]; Timestamp [string]; Memo [string]
        Status [string] 'OrderSent' (receipt arrives asynchronously).
        Success [bool] Always $true (throws on failure).
.NOTES
    C# API: ISvrn7SocietyDriver.TransferToExternalCitizenAsync(TransferRequest, string)
    Spec:   draft-herman-didcomm-svrn7-transfer-00 §8
            draft-herman-svrn7-monetary-protocol-00 §7.2
#>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium',
                   DefaultParameterSetName = 'BySvrn7')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)] $SocietyDriver,
        [Parameter(Mandatory)] [string]        $PayerDid,
        [Parameter(Mandatory)] [PSCustomObject] $PayerKeyPair,
        [Parameter(Mandatory)] [string]        $PayeeDid,
        [Parameter(Mandatory)] [string]        $TargetSocietyDid,

        [Parameter(Mandatory, ParameterSetName = 'BySvrn7')]
        [ValidateRange(0.000001, 1e15)]
        [double] $AmountSvrn7,

        [Parameter(Mandatory, ParameterSetName = 'ByGrana')]
        [ValidateRange(1L, [long]::MaxValue)]
        [long] $AmountGrana,

        [Parameter()]
        [ValidateLength(0, 256)]
        [string] $Memo = '',

        [Parameter()]
        [string] $Nonce = ''
    )

    $grana = if ($PSCmdlet.ParameterSetName -eq 'BySvrn7') {
        [long][Math]::Round($AmountSvrn7 * 1000000)
    } else { $AmountGrana }
    $svrn7          = [decimal]$grana / 1000000
    $effectiveNonce = if ($Nonce) { $Nonce } else { [Guid]::NewGuid().ToString('N') }
    $timestamp      = [DateTimeOffset]::UtcNow.ToString('O')
    $memo           = if ($Memo) { $Memo } else { $null }

    $json    = Build-CanonicalTransferJson $PayerDid $PayeeDid $grana $effectiveNonce $timestamp $memo
    $payload = [System.Text.Encoding]::UTF8.GetBytes($json)
    $sig     = $SocietyDriver.SignSecp256k1($payload, $PayerKeyPair.PrivateKeyBytes)

    Write-Verbose "Cross-Society transfer: $svrn7 SVRN7 → $PayeeDid via $TargetSocietyDid"

    if (-not $PSCmdlet.ShouldProcess($PayerDid,
            "Transfer $svrn7 SVRN7 to $PayeeDid (Society: $TargetSocietyDid)")) { return }

    $request = [Svrn7.Core.Models.TransferRequest]@{
        PayerDid    = $PayerDid
        PayeeDid    = $PayeeDid
        AmountGrana = $grana
        Nonce       = $effectiveNonce
        Timestamp   = [DateTimeOffset]::Parse($timestamp)
        Signature   = $sig
        Memo        = $memo
    }

    $result = $SocietyDriver.TransferToExternalCitizenAsync(
        $request, $TargetSocietyDid).GetAwaiter().GetResult()
    Resolve-OperationResult -Result $result -Operation 'ExternalTransfer' | Out-Null

    $txId = $SocietyDriver.Blake3HexAsync($payload).GetAwaiter().GetResult()
    Write-Verbose "Cross-Society order sent. TransferId: $txId"

    [PSCustomObject]@{
        PSTypeName       = 'Svrn7.ExternalTransferResult'
        TransferId       = $txId
        PayerDid         = $PayerDid
        PayeeDid         = $PayeeDid
        TargetSocietyDid = $TargetSocietyDid
        AmountGrana      = $grana
        AmountSvrn7      = $svrn7
        Nonce            = $effectiveNonce
        Timestamp        = $timestamp
        Memo             = $Memo
        Status           = 'OrderSent'
        Success          = $true
    }
}

function Invoke-Svrn7FederationTransfer {
<#
.SYNOPSIS
    Transfers SVRN7 from a citizen in this Society to the Federation wallet.
.DESCRIPTION
    Wraps ISvrn7SocietyDriver.TransferToFederationAsync() on the supplied driver.
    Permitted in both Epoch 0 (where citizen-to-Federation is one of only two allowed
    payees) and Epoch 1. This makes it the only cross-boundary transfer available before
    the ecosystem reaches Epoch 1.

    The payer signs the canonical JSON (field order: PayerDid, PayeeDid, AmountGrana,
    Nonce, Timestamp, Memo) with their secp256k1 private key. The transfer is validated
    through the standard eight-step pipeline.
.PARAMETER SocietyDriver
    An initialised ISvrn7SocietyDriver — e.g. from Connect-Svrn7Society -PassThru
    (Svrn7.Society.0.8.0.psm1).
.PARAMETER PayerDid
    DID of the payer. Must be an active citizen of this Society.
.PARAMETER PayerKeyPair
    Svrn7.KeyPair (secp256k1) for the payer.
.PARAMETER AmountSvrn7
    Amount in SVRN7. Mutually exclusive with -AmountGrana.
.PARAMETER AmountGrana
    Amount in grana. Mutually exclusive with -AmountSvrn7.
.PARAMETER Memo
    Optional memo, maximum 256 characters.
.PARAMETER Nonce
    Optional idempotency nonce. Auto-generated if omitted.
.OUTPUTS
    PSCustomObject [Svrn7.FederationTransferResult]
        PayerDid [string]; PayeeDid [string] (the Federation wallet DID)
        AmountGrana [long]; AmountSvrn7 [decimal]; Nonce [string]
        Timestamp [string]; Memo [string]; Success [bool]
.NOTES
    C# API: ISvrn7SocietyDriver.TransferToFederationAsync(string, long, string, string, string?)
#>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium',
                   DefaultParameterSetName = 'BySvrn7')]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory)] $SocietyDriver,
        [Parameter(Mandatory)] [string]        $PayerDid,
        [Parameter(Mandatory)] [PSCustomObject] $PayerKeyPair,

        [Parameter(Mandatory, ParameterSetName = 'BySvrn7')]
        [ValidateRange(0.000001, 1e15)]
        [double] $AmountSvrn7,

        [Parameter(Mandatory, ParameterSetName = 'ByGrana')]
        [ValidateRange(1L, [long]::MaxValue)]
        [long] $AmountGrana,

        [Parameter()]
        [ValidateLength(0, 256)]
        [string] $Memo = '',

        [Parameter()]
        [string] $Nonce = ''
    )

    $grana = if ($PSCmdlet.ParameterSetName -eq 'BySvrn7') {
        [long][Math]::Round($AmountSvrn7 * 1000000)
    } else { $AmountGrana }
    $svrn7          = [decimal]$grana / 1000000
    $effectiveNonce = if ($Nonce) { $Nonce } else { [Guid]::NewGuid().ToString('N') }
    $timestamp      = [DateTimeOffset]::UtcNow.ToString('O')
    $memo           = if ($Memo) { $Memo } else { $null }

    # Derive Federation DID from the Society's own record
    $soc    = $SocietyDriver.GetOwnSocietyAsync().GetAwaiter().GetResult()
    $fedDid = if ($soc -and $soc.FederationDid) { $soc.FederationDid } else { 'did:drn:federation' }

    # Sign canonical JSON
    $json    = Build-CanonicalTransferJson $PayerDid $fedDid $grana $effectiveNonce $timestamp $memo
    $payload = [System.Text.Encoding]::UTF8.GetBytes($json)
    $sig     = $SocietyDriver.SignSecp256k1($payload, $PayerKeyPair.PrivateKeyBytes)

    if (-not $PSCmdlet.ShouldProcess($PayerDid, "Transfer $svrn7 SVRN7 to Federation")) { return }

    $result = $SocietyDriver.TransferToFederationAsync(
        $PayerDid, $grana, $effectiveNonce, $sig, $memo).GetAwaiter().GetResult()
    Resolve-OperationResult -Result $result -Operation 'FederationTransfer' | Out-Null

    Write-Verbose "Federation transfer committed: $grana grana"

    [PSCustomObject]@{
        PSTypeName  = 'Svrn7.FederationTransferResult'
        PayerDid    = $PayerDid
        PayeeDid    = $fedDid
        AmountGrana = $grana
        AmountSvrn7 = $svrn7
        Nonce       = $effectiveNonce
        Timestamp   = $timestamp
        Memo        = $Memo
        Success     = $true
    }
}
#endregion

Export-ModuleMember -Function @(
    'New-Svrn7KeyPair'
    'Invoke-Svrn7SignSecp256k1'
    'Invoke-Svrn7Transfer'
    'Invoke-Svrn7BatchTransfer'
    'Invoke-Svrn7ExternalTransfer'
    'Invoke-Svrn7FederationTransfer'
)
