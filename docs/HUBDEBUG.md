# WebSocketNotifyHub — Debug & Testing Guide

This guide covers manually exercising `/didcomm-ws` (Hello handshake, subscription-based
broadcast routing, correlation-based reply routing) and the PandoMail folder-count / Cc
fan-out behavior that rides on top of it, end-to-end against a live TDA. No unit-test
stubs — real `WebSocketNotifyHub`, real `KestrelListenerService`, real LOBEs.

See `docs/BACKLOG.md` TDA-011 for the design this exercises, and
`src/Svrn7.TDA/WebSocketNotifyHub.cs` for the implementation.

---

## Prerequisites

- PowerShell 7 (`pwsh.exe`) is required for the scripted WebSocket client below.

```powershell
$PSVersionTable.PSVersion   # Major must be 7
```

- **`WebSocketNotifyHub`'s log category must be enabled** or none of this is visible —
  it defaults to `Warning` via `"Default"` in `appsettings.json`, which silently
  suppresses every Hello/Subscribed/push-routing log line. Confirm this entry exists in
  `src/Svrn7.TDA/appsettings.json`:

```json
"Svrn7.TDA.WebSocketNotifyHub": "Debug",
```

If it's missing, add it next to the `Svrn7.TDA.KestrelListenerService` entry and rebuild.
Without it you'll see the WebSocket attach/detach and raw frame bytes, but nothing about
Hello parsing, subscription matching, or correlated-reply routing — which looks exactly
like a silent failure even when everything is working correctly.

---

## Step 1 — Build and start a fresh TDA

```powershell
Set-Location C:/SVRN7/repos/SVRN7
dotnet build src/Svrn7.TDA/Svrn7.TDA.csproj

Set-Location src/Svrn7.TDA/bin/Debug/net8.0
dotnet .\Svrn7.TDA.dll --port 8443 --name VerifyTDA --reset
```

`--reset` wipes `8443/mem` so folder counts start at zero — makes the before/after deltas
in later steps unambiguous. Note the `Agent DID` printed at startup banner; the scripts
below need it for a self-addressed send.

Confirm in the startup log:

```
KestrelListenerService: POST /didcomm (HTTP/2 inbound) and GET /didcomm-ws (WebSocket RFC 8441) active on port 8443.
```

If this says `/didcomm-notify` instead of `/didcomm-ws`, the build predates the path
rename — rebuild.

---

## Step 2 — Prove the real PandoMail client sends Hello correctly

This is the highest-value single check: it confirms `TdaMailClient.SendHelloAsync` (the
actual shipped code, not a test double) round-trips correctly against the real hub.

```powershell
Start-Process "C:\SVRN7\repos\SVRN7\src\Web7.SVRN7.Apps.PandoMail\C#\OLAF\bin\Debug\net8.0-windows\PandoMail.exe"
```

Watch the TDA console (or tail its log) for, in order:

```
info: Svrn7.TDA.KestrelListenerService[0]
      KestrelListenerService: local-UI WebSocket attached on /didcomm-ws (id=<guid>).
info: Svrn7.TDA.WebSocketNotifyHub[0]
      WebSocketNotifyHub: Hello from app='PandoMail' version='<version>' instance=<guid> (2 subscription(s)).
```

"2 subscription(s)" is `TdaMailClient.Subscriptions` — `Email-Notify.0.1.0/` (prefix) and
`PandoMail.0.8.0/Notify-FolderCounts` (exact). If this line never appears, either Hello
never arrived (client bug) or `TryHandleControlFrameAsync` swallowed it (check for a
`WebSocketNotifyHub: could not parse WebSocket frame as JSON` debug line right after).

As PandoMail's UI does its normal startup queries, you should also see a stream of
`(correlated reply) type=...` lines for `Reply-TdaDid`, `Get-PandoMails`, `Get-PandoOutbox`,
`Get-PandoDeadLetters`, `Reply-DidDocument`, etc. — confirms correlation-based unicast
routing (not subscription filtering) is what's delivering these request/reply pairs.

---

## Step 3 — Scripted WebSocket client (no GUI required)

For everything below the GUI layer — Cc fan-out, folder-count math, dead-letter content —
a raw `ClientWebSocket` script is faster and more precise than clicking through Compose.
This is the same H2C WebSocket technique `Send-LocalDIDCommMessage` (in `Svrn7.Common`)
already uses.

Save as `verify-cc.ps1`, substituting the `Agent DID` from Step 1's startup banner:

```powershell
[System.AppContext]::SetSwitch('System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport', $true)

$tdaDid = "did:drn:wanderer.svrn7.net/agent/1.0/<paste-from-startup-banner>"
$uri = "ws://localhost:8443/didcomm-ws"

$handler = [System.Net.Http.SocketsHttpHandler]::new()
$invoker = [System.Net.Http.HttpClient]::new($handler)
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.HttpVersion = [System.Version]::new(2,0)
$ws.Options.HttpVersionPolicy = [System.Net.Http.HttpVersionPolicy]::RequestVersionOrHigher
$ws.ConnectAsync([Uri]::new($uri), $invoker, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
Write-Host "Connected."

function Send-Frame($obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 6
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $ws.SendAsync([System.ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    Write-Host "SENT: $json"
}

function Receive-Frame($timeoutMs) {
    $buffer = New-Object byte[] 16384
    $cts = [System.Threading.CancellationTokenSource]::new($timeoutMs)
    try {
        $result = $ws.ReceiveAsync([System.ArraySegment[byte]]::new($buffer), $cts.Token).GetAwaiter().GetResult()
        $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)
        Write-Host "RECV: $text"
        return $text
    } catch {
        Write-Host "RECV: (timeout, no frame)"
        return $null
    }
}

# 1. Hello — declare identity + subscriptions, matching what TdaMailClient sends.
Send-Frame @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/Svrn7.LocalUI.0.1.0/Hello"
    body = @{
        app = "VerifyScript"
        appVersion = "1.0"
        instanceId = [Guid]::NewGuid().ToString()
        subscriptions = @(
            @{ uri = "did:drn:svrn7.net/protocols/Email-Notify.0.1.0/"; match = "prefix" }
            @{ uri = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Notify-FolderCounts"; match = "exact" }
        )
    }
}
Receive-Frame 3000 | Out-Null   # expect Svrn7.LocalUI.0.1.0/Subscribed, echoing the 2 subscriptions back

# 2. Enqueue-PandoMail: To = self (resolvable, tests self-delivery + Email-Notify),
#    Cc = one nonexistent DID (tests independent per-recipient dead-lettering).
Send-Frame @{
    typ  = "application/didcomm-plain+json"
    id   = "did:drn:svrn7.net/didcomm/msg/$([Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/Enqueue-PandoMail"
    body = @{
        recipientDid = $tdaDid
        subject      = "Verify Cc fan-out"
        bodyText     = "hello from verify script"
        cc           = "did:drn:doesnotexist.svrn7.net/citizen/nobody"
    }
}

Write-Host "--- waiting for pushes ---"
for ($i = 0; $i -lt 5; $i++) { Receive-Frame 3000 | Out-Null }

$ws.Dispose()
$invoker.Dispose()
```

### Expected output

```
RECV: {...,"type":"...Svrn7.LocalUI.0.1.0/Subscribed","body":{"subscriptions":[...both entries echoed...]}}
RECV: {...,"type":"...PandoMail.0.8.0/Notify-FolderCounts",...,"body":{"inboxCount":0,"sentCount":<N+1>,"deadLetterCount":<M+1>}}
RECV: {...,"type":"...Email-Notify.0.1.0/new-message",...,"body":{"subject":"Verify Cc fan-out",...}}
RECV: {...,"type":"...PandoMail.0.8.0/Notify-FolderCounts",...,"body":{"inboxCount":1,"sentCount":<N+1>,"deadLetterCount":<M+1>}}
```

What to check:
- **Subscribed ack** echoes exactly the two declared subscriptions — confirms Hello
  parsing and the ack round-trip.
- **First FolderCounts push** — `deadLetterCount` increments by 1 (the unresolvable Cc),
  `sentCount` increments by 1 (the one Enqueue-PandoMail request), `inboxCount` still 0
  (the self-delivered copy hasn't round-tripped back yet).
- **Email-Notify** — proves the To=self copy was actually delivered and
  `Dequeue-PandoMail` fired the notification, correctly subscription-matched against this
  script's `Email-Notify.0.1.0/` prefix subscription (a *different* connection than
  PandoMail's — if this arrives, subscription-based multicast isn't hardcoded to one app).
- **Second FolderCounts push** — `inboxCount` goes 0→1 once the self-delivered message is
  actually processed. `sentCount`/`deadLetterCount` unchanged from the first push — both
  pushes reflect the same underlying state, from two different LOBE code paths
  (`Enqueue-PandoMail`'s own `New-FolderCountsNotification` call, then
  `Dequeue-PandoMail`'s).

If `deadLetterCount`/`sentCount` don't start at 0 on a fresh `--reset` TDA, someone else
(a real user poking at a concurrently-open PandoMail window, or a leftover process) has
already sent traffic — check deltas, not absolute values, unless you know nothing else is
connected.

---

## Step 4 — Cross-check against the real list queries

Confirms the pushed `Notify-FolderCounts` numbers match what `List-DeadLetters` /
`List-OutboundEmails` actually return — i.e. the count and the content agree.

```powershell
# reuse the Send-Frame / Receive-Frame functions from Step 3, on a fresh connection

Send-Frame @{
    typ = "application/didcomm-plain+json"; id = "did:drn:svrn7.net/didcomm/msg/$([Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-DeadLetters"
    body = @{ correlationId = [Guid]::NewGuid().ToString('N') }
}
Receive-Frame 5000 | Out-Null   # Get-PandoDeadLetters — "count" must equal the FolderCounts deadLetterCount

Send-Frame @{
    typ = "application/didcomm-plain+json"; id = "did:drn:svrn7.net/didcomm/msg/$([Guid]::NewGuid().ToString('N'))"
    type = "did:drn:svrn7.net/protocols/PandoMail.0.8.0/List-OutboundEmails"
    body = @{ correlationId = [Guid]::NewGuid().ToString('N') }
}
Receive-Frame 5000 | Out-Null   # Get-PandoOutbox — "count" must equal the FolderCounts sentCount
```

Each dead-letter entry's `subject` is `"FAILED: <error>"` — for an unresolvable recipient
this reads `FAILED: No DIDComm service endpoint found for recipient '<recipient>'`, one
entry per failed recipient (To or Cc) — confirms per-recipient independence, not an
all-or-nothing send.

---

## Step 5 — Correlation-unicast vs. subscription-multicast (two connections)

To see the difference between the two routing tiers live, open **two** scripted
connections (Step 3's Hello block, different `instanceId` each) or one script window plus
a real PandoMail instance:

- Send `List-DeadLetters` from connection A only. In the TDA log, the reply
  (`Get-PandoDeadLetters`) should show `(correlated reply) type=...`, and it must
  **only** be sent to connection A's socket — connection B receives nothing for it,
  regardless of B's declared subscriptions (correlation match happens before
  subscription filtering is even considered).
- Trigger a `Notify-FolderCounts` push (e.g. by sending anything that dead-letters).
  Both A and B receive it if both declared the `PandoMail.0.8.0/Notify-FolderCounts`
  subscription at Hello — no `(correlated reply)` tag, since this path has no
  correlationId.

This scenario is also covered by `WebSocketNotifyHubTests.Correlated_Reply_Is_Unicast_To_Requesting_Connection_Only`
in `tests/Svrn7.TDA.Tests/TdaTests.cs`, with a stub `IDIDCommService` — this manual
version exercises the same logic through real LOBEs.

---

## Known log-reading gotchas

- **`WebSocketNotifyHub` logs at `Information`/`Debug`** — invisible unless the
  `appsettings.json` entry from Prerequisites is present. This bit real verification once
  already (see `docs/BACKLOG.md` TDA-011/013 history) — four minutes were lost concluding
  Hello was silently failing when it was actually just unlogged.
- **`ProcessWebSocketMessageAsync` runs fire-and-forget** (`_ = Task.Run(...)`) — an
  unhandled exception inside `TryHandleControlFrameAsync` or the normal unpack/enqueue path
  is never surfaced to the caller. If Hello appears to do nothing, check for a
  `WebSocketException` or `ObjectDisposedException` at `Debug` level before assuming a
  logic bug in the matching itself.

---

## Verification log — 2026-07-01

Ran this regime live against a fresh `VerifyTDA` (port 8443, `--reset`) and the real
`PandoMail.exe`. No repo-specific verifier skill existed at the time; no GUI-automation
tool was available for this native WinForms app, so the client-side pixel behavior
(folder-tree highlight, textbox parsing) was not clicked through directly.

**Verdict:** PASS, with one real bug found and fixed along the way, and one sub-claim not
reachable in this environment.

**Steps and results:**

1. ✅ Built and started TDA fresh → startup log confirmed
   `GET /didcomm-ws (WebSocket RFC 8441) active on port 8443` — the path rename is live,
   not just in source.
2. ✅ Launched real `PandoMail.exe` → TDA attached the socket, but no further activity
   appeared in the log at all.
3. 🔍 Investigated the silence — `WebSocketNotifyHub` had no `appsettings.json` log-level
   entry (fell back to `Default: Warning`, unlike `KestrelListenerService`/
   `DIDCommMessageSwitchboard`, which have explicit `Debug` entries). Every Hello/
   Subscribed/routing log written this session was silently suppressed — not a functional
   bug, but it made the feature look broken when it wasn't. Fixed by adding the entry
   documented in Prerequisites above, then rebuilt and relaunched.
4. ✅ Re-launched PandoMail → TDA log showed
   `WebSocketNotifyHub: Hello from app='PandoMail' version='unknown' instance=<guid> (2 subscription(s))`
   — the real shipped `TdaMailClient.SendHelloAsync` code, not a test double, correctly
   performs the handshake.
5. ✅ PandoMail's normal startup traffic (`Query-TdaDid`, `List-Emails`,
   `List-OutboundEmails`, `List-DeadLetters`, `Resolve-PandoDid`) all logged as
   `(correlated reply)` routed to its own connection — confirms correlation-based unicast
   is what actually delivers replies, not subscription filtering.
6. ✅ Scripted a second, independent WebSocket connection (`VerifyScript`): sent `Hello` →
   got `Subscribed` echoing back the exact 2 declared subscriptions.
7. ✅ Sent `Enqueue-PandoMail` with `recipientDid` = the TDA's own DID (self, resolvable)
   and `cc` = an unresolvable DID → received, in order: a `Notify-FolderCounts` push
   (`deadLetterCount` +1, `sentCount` +1), then `Email-Notify.0.1.0/new-message` (subject
   matched exactly), then a second `Notify-FolderCounts` (`inboxCount` 0→1 once the
   self-delivered copy was actually processed).
8. ✅ Cross-checked via `List-DeadLetters`/`List-OutboundEmails` on the same connection —
   returned counts (5 and 2 respectively at that point) exactly matched the numbers just
   pushed via `Notify-FolderCounts`.
9. ⚠️ Unplanned but valuable: while this was running, the real user interacted with the
   live PandoMail window themselves — the dead-letter list contained entries for
   recipients `"Foo"`, `"bar"`, and a mistyped DID (missing the leading `d` in `did:`),
   each as its own independent dead-letter record. This is the exact semicolon-separated
   multi-recipient Cc scenario from earlier design work, confirmed working through the
   actual GUI by an actual human, not a script.
10. 🔍 Probed the size cap / idle-watchdog additions indirectly — no size-cap rejections
    or idle timeouts fired during normal use (expected; nothing sent was near 1&nbsp;MB or
    idle 60s+), so these are confirmed present and non-disruptive to normal traffic but
    not exercised at their boundary in this pass.

**Sample capture:**

```
RECV: {"type":"...Svrn7.LocalUI.0.1.0/Subscribed","body":{"subscriptions":[
  {"uri":"...Email-Notify.0.1.0/","match":"prefix"},
  {"uri":"...PandoMail.0.8.0/Notify-FolderCounts","match":"exact"}]}}
RECV: {"type":"...PandoMail.0.8.0/Notify-FolderCounts","body":{"inboxCount":0,"sentCount":2,"deadLetterCount":5}}
RECV: {"type":"...Email-Notify.0.1.0/new-message","body":{"subject":"Verify Cc fan-out", ...}}
RECV: {"type":"...PandoMail.0.8.0/Notify-FolderCounts","body":{"inboxCount":1,"sentCount":2,"deadLetterCount":5}}
```

**Findings:**

- ⚠️ `WebSocketNotifyHub` had no `appsettings.json` log-level entry — fixed (see
  Prerequisites) and now permanent so this doesn't recur.
- ⚠️ Not verified: folder-tree highlight sync, Cc textbox parsing, and unread-count
  scoping at the pixel level — no GUI-automation tool available for this native WinForms
  app in this environment. The real user's own interaction (step 9) is the closest
  evidence for the Cc-parsing half, and it checked out; tree-selection-sync and
  unread-count scoping remain backed only by code reading and their unit test, not a
  live click-through.
- `appVersion='unknown'` in PandoMail's Hello — `AssemblyInformationalVersionAttribute`
  isn't set on PandoMail's csproj, so it falls back correctly, but the field is currently
  unusable for build-traceability purposes (the reason `WsExample2` includes it).
  Cosmetic, not a defect.
