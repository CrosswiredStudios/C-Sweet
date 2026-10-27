# One-shot trusted-host RealtyLeads probe

This executable tests the actual `AccountVault` and `RemoteMcpBroker`. It is not an
agent, a public service, a replacement for native account authentication, or the
fake-auth browser acceptance harness. No provider email/identity endpoint is called.

After explicit authorization for live reads, run from the workspace:

```sh
dotnet C-Sweet/tools/CSweet.RemoteMcp.Probe/bin/Debug/net10.0/CSweet.RemoteMcp.Probe.dll
```

Enter the temporary key only at the hidden local terminal prompt. Do not put it in
chat, commands/arguments, shell history, environment variables, files or logs. The
optional `--stdin` mode is for an approved trusted sender over a private anonymous
pipe; do not use shell `echo`, file redirection or a credential-bearing command.
The program ignores no unknown arguments: it rejects them without repeating them.

One search requests limit 1. Zero leads skips lookup; one lead permits one lookup
of that returned ID. Unexpected results fail closed. Only status, count, lookup
success, local revocation status, allowlisted failure stage/category and numeric HTTP
status are printed, never lead fields, IDs, key values, remote text or exception details. No automatic retry or privilege/policy change occurs.

The key and explicit test grant use an ephemeral in-memory repository and ephemeral
Data Protection. Nothing persists a key ring, credential or grant. Scope IDs identify
only this local test, not a verified C-Sweet business or provider account. A hard
10-minute process watchdog includes input time; ordinary cancellation/failure revokes
in `finally`, and successful cleanup verifies denial before any further transport.
Process exit removes ephemeral state; managed strings are not guaranteed to be
zeroed immediately while the trusted process is alive.

Only the production broker constructs outbound HTTPS requests to the fixed endpoint
https://realtyleads.ai/mcp, with normal TLS validation. No listener, database,
container, compute node, persistent C-Sweet grant or deployment is created. The
provider's key remains valid until the human revokes it at RealtyLeadsAI. A generic
failure does not prove an admin-role rejection or a particular account identity.

Verification: 47 broker/probe tests pass with synthetic keys and fake MCP responses.
CLI checks also confirm invalid synthetic stdin is rejected without echo/network
and terminal input remains hidden and cancels before dispatch. No live provider
request was made during those checks.

Live handoff status: the available conversation reader returned zero turns and no
pagination for the parent thread, so the original authorized secret message could
not be retrieved. Do not work around this by reading hidden application logs.
Use the masked terminal prompt above; do not ask the human to paste the key in chat.

## First user-entered run and diagnostic correction

The first run exited with generic failure, zero released results and verified local
revocation. Its output cannot prove whether an HTTP request reached the server or
which stage failed. The input passed key-format validation; this was not the
empty/invalid-input path. Buffered paste-style input was subsequently verified with
invalid synthetic text in a terminal, without using the clipboard or making a request.

A key-free DNS check found two public A and two public AAAA answers. The previous
transport rejected any IPv6 answer before connecting. The corrected policy selects
only public IPv4 addresses, rejects unsafe A answers and IPv6-only resolution, and
retains exact origin, TLS and redirect protections. This is a plausible local cause
of the first failure, not retrospective proof of its HTTP outcome.

The updated probe reports fixed enum categories and stages plus HTTP status where
received. 401 means authentication rejected; 403 means access policy rejected. Neither
proves a particular role or provider company identity. No provider policy is changed.
No live retry is authorized by this diagnostic update. On the next approved opening,
launch the wrapper with `exec /bin/zsh ...` so its terminal cannot return to an accepting
shell after probe exit. Never paste a key unless a fresh hidden probe prompt is confirmed.
