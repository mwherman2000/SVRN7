#Requires -Version 7.2
<#
.SYNOPSIS
    SVRN7 Trust AppAuthn LOBE — generic local-UI wallet-password verification.

.DESCRIPTION
    Implements the did:drn:svrn7.net/protocols/Svrn7.Trust.AppAuthn.0.1.0/*
    DIDComm protocol. Any local UI client (PandoMail's TDA picker, a future
    PandoBoard, admin tooling, etc.) can use this to verify that the human
    at the keyboard knows this TDA's wallet password before treating the
    connection as "signed in" — without the TDA ever handing back key
    material. Verification happens entirely inside $SVRN7.VerifyWalletPassword
    (Svrn7RunspaceContext), which re-runs the same Argon2id + AES-256-GCM
    decrypt Program.cs performs at bootstrap and discards the result
    immediately; only a pass/fail (and a human-readable reason on failure)
    ever crosses back over /localcomm-ws.

    Named alongside Svrn7.Trust.AgentWallet (the wallet library this LOBE's
    verification ultimately calls into) — deliberately generic, not named
    after PandoMail, so any local-UI app sharing this TDA's identity can
    reuse the same protocol.

.NOTES
    Protocol (inbound):  did:drn:svrn7.net/protocols/Svrn7.Trust.AppAuthn.0.1.0/Authenticate
    Protocol (outbound): did:drn:svrn7.net/protocols/Svrn7.Trust.AppAuthn.0.1.0/AuthResult

    Not a server-side access gate: /localcomm-ws still accepts all local
    traffic regardless of Authenticate's outcome (P-008; see docs/BACKLOG.md
    for the discussion of a future AuthZ gate for *external* TDA traffic in
    DrawbridgeService — a separate, unrelated concern). The gate here is
    client-side: PandoMail's own UI won't proceed past its password prompt
    without a successful AuthResult.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Invoke-Svrn7TrustAppAuthnAuthenticate ─────────────────────────────────────

function Invoke-Svrn7TrustAppAuthnAuthenticate {
    <#
    .SYNOPSIS
        Verifies a candidate wallet password and replies with AuthResult.

    .DESCRIPTION
        Handles an Authenticate request from a local UI client. Body:
        { password: string }. Replies with AuthResult over the WebSocket push
        channel (envelope thid = this request's WireId, so the requesting
        connection — not every connected local-UI client — receives it).

    .PARAMETER MessageDid
        The TDA resource DID URL of the inbox message.

    .OUTPUTS
        [Svrn7.TDA.OutboundMessage] delivering AuthResult over WebSocket.
    #>
    [CmdletBinding()]
    [OutputType([Svrn7.TDA.OutboundMessage])]
    param(
        [Parameter(Mandatory, ValueFromPipelineByPropertyName)]
        [string] $MessageDid
    )

    process {
        $msg = $SVRN7.GetMessageAsync($MessageDid).GetAwaiter().GetResult()
        if (-not $msg) {
            Write-Warning "Trust.AppAuthn LOBE: Authenticate message $MessageDid not found."
            return $null
        }

        $body     = $msg.PackedPayload | ConvertFrom-Json -ErrorAction Stop
        $password = Get-BodyField $body 'password' ''

        $verifyResult   = $SVRN7.VerifyWalletPassword($password)
        $authenticated  = $verifyResult.Authenticated
        $reason         = $verifyResult.Reason

        Write-Verbose "Trust.AppAuthn LOBE: Authenticate result — authenticated=$authenticated"

        $envelope = [ordered]@{
            typ  = 'application/didcomm-plain+json'
            id   = [Svrn7.Core.TdaResourceId]::DIDCommMessage([Guid]::NewGuid().ToString('N'))
            thid = $msg.WireId
            type = 'did:drn:svrn7.net/protocols/Svrn7.Trust.AppAuthn.0.1.0/AuthResult'
            from = $SVRN7.LocalDid
            to   = @($SVRN7.LocalDid)
            body = [ordered]@{
                authenticated = $authenticated
                reason        = $reason
            }
        } | ConvertTo-Json -Compress -Depth 3

        [Svrn7.TDA.OutboundMessage]::new('ws://local/localcomm-ws', $envelope)
    }
}

Export-ModuleMember -Function @(
    'Invoke-Svrn7TrustAppAuthnAuthenticate'
)
