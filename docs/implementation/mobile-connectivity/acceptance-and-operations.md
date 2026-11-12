# Mobile connectivity acceptance, operations and progress

Date: 2026-11-11. **Documentation only; all implementation milestones pending.**
Read the [plan](README.md) and [architecture](architecture.md) first. Evidence here must
distinguish source/unit tests, separate-process integration, physical-device acceptance
and deployed-service acceptance. Do not infer one from another.

## Progress register

This is the single execution record. M0–M6 track relay and pilot delivery. P1/P2/P4–P7 below cover the additional product outcomes. Phase 0 uses M0, Phase 3 uses M1 and M2. The phase guides describe work and the matrix defines tests; enter actual status, runs and release decisions here. A partial pilot can close M6 while general release remains pending.

| Package | Status | Required evidence |
| --- | --- | --- |
| M0 · Phase 0 baseline/contracts/TLS spike | Source version matched; hardware gate pending | 0.7.0 nuspec and matching source commit in architecture. Attach workflow inventory review, limits and Android/iOS × Windows/Linux stream proof. |
| M1: standalone relay | Not started | Service HTTP/SignalR isolation, enrollment, quotas and container installation without C-Sweet identity/OAuth. |
| M2: settings/pairing/device lifecycle | Not started | Migration, local access, invitation races, durable relay registration and revocation tests. |
| M3: read-only mobile slice | Not started | Physical phone reads/events/reconnect behind NAT; authorization conformance. |
| M4: safe beta actions | Not started | Durable once-only chat/approval effects through response loss/restarts. |
| M5: operations/hosting parity | Not started | Capacity, restore, relay switching, entitlements and privacy evidence. |
| M6 · Phase 7 bounded pilot | Not started | Published pilot identifiers, installation, explicit workflow coverage and monitored deployment evidence. |
| P1 · Phase 1 mobile shell | Pending | Fixture IDs, route assertions, empty/offline/approval screenshots, Back and accessibility results. |
| P2 · Phase 2 accounts and membership | Pending | Invitation replay/removal tests, scoped access from web and mobile, local recovery, independent IdP subjects, preserved Market records. |
| MU · Multi-user Headquarters (Phase 2) | Pending | Empty endpoint-access allowlist, conformance test green with production policies, Games-only member denied Media through every list, route and event, last-owner rules, unchanged upgrade. |
| IL · Accounts and installation linking (Phase 2) | Pending | Linked first run creating the administrator's C-Sweet account, private first run with zero outbound requests, existing Market customer sign-in with purchases intact, real confirmation email, link and unlink of an existing installation. |
| TS · Request tunnel and thin-shell trial (Phases 3–4) | Pending | Dispatcher tests (unmarked route, stripped headers, revoked mid-stream), local proxy rejects calls without the secret, per-profile storage isolation, trial table results on both phones and the recorded outcome. |
| PN · Push notifications (Phases 0, 3, 5) | Pending | Closed-app delivery on both platforms, captured payload free of business identifiers, no wake after revoke, isolated network makes no push connections. |
| P4 · Phase 4 integrated product, builds on M3 | Pending | Shared discovery plus two-HQ real-device sessions, same-name businesses, A→B→A late events and reconnect. |
| P5 · Phase 5 daily work, builds on M4 | Pending | Daily matrix workflows, same-actor web comparisons, durable command receipts and file cancellation/recovery. |
| P6 · Phase 6 administration | Pending | Admin/member matrix, real provider returns and file imports, failures and permission changes. |
| P7 · Phase 7 general release, includes M5 operations | Pending | Complete workflow evidence on both phone platforms, private isolated runs, compatibility, restore, accessibility and operating sign-off. |

Existing onboarding/provider tests do not count as completed mobile milestones.
For each implementation checkpoint record source revisions, exact protocol/package
versions, environment/platform versions, test commands/results/skips, evidence location,
unresolved failures and next milestone. Preserve failed runs and avoid stale version claims.

### Recording a run

Append one evidence row per run, linking all covered workflow IDs. Use the same run ID in the phase handoff. Keep fixture, separate-process, physical-device and deployed-service results distinguishable. Mark unavailable hardware as pending. A filtered test run with zero tests is inconclusive.

| Run ID / date | Phase, M gate and workflow IDs | Environment, actor, identity and topology | Component revisions / versions | Commands or manual steps, expected and observed result | Evidence, failures and next owner |
| --- | --- | --- | --- | --- | --- |
| No implementation runs recorded | — | — | — | — | — |

### Release artifacts to verify

| Owner / artifact | Install and compatibility evidence |
| --- | --- |
| MAUI Android and iOS | Signed candidate identifiers, supported OS/device matrix, upgrade preserving only valid profiles, hardware Back/swipe and secure-store/reinstall behavior. |
| Headquarters | Fresh/upgrade migrations, initial-root recovery, disabled mobile default, policy and authorization parity, supported mobile catalog versions. |
| C-Sweet account (Market issuer) | Issuer and client registrations on the account hostname, verified email/recovery, existing customers keep user ID, purchases and roles, device authorization for Headquarters, signing key rotation and rejection of invalid issuers. |
| Push gateway | Apple and Google credentials in protected configuration, no payload logging, rejected-token feedback, self-hosted relay registration. |
| Optional directory | Authorized own-enterprises discovery, outbox lag/reconcile, removal tombstones, already-enrolled behavior during outage, zero public directory traffic in private deployments. |
| Relay and protocol | Published package metadata and package-only consumer, standalone official and private installation, upgrade/rollback, negotiated capabilities, no mandatory provider OAuth secrets. |

## Automated acceptance

| Area | Required scenarios |
| --- | --- |
| Transport/privacy | Outer HTTPS validation; inner Headquarters pin mismatch; fake relay redirects; modified/replayed/out-of-order ciphertext; invalid/expired device cert; MITM; incompatible protocol; malformed frames; cancellation/partial frames; slow receiver and queue exhaustion. Assert relay cannot decode seeded content. |
| Admission/isolation | HQ connector versus phone role substitution; cross-tenant/route/channel IDs; expired/replayed nonce; old binding generation; stolen admission token without device key; duplicate HQ attachment fenced; unauthorized operator enrollment. |
| Pairing | Valid QR/manual flow; malformed/oversized invitation; expired/cancelled secret; duplicate scan; two phones racing; same phone retries after lost response; approval by authorized delegate versus unauthorized/disabled user; expiry during confirmation; crash before/after commit; delayed registration after revoke. |
| Authorization | Same user/resource decisions through web and mobile; private conversations; changed business/chat membership while connected; role downgrade; disabled/locked user; changed security stamp; forbidden reads, events and result receipts; no client-supplied actor authority. |
| Event recovery | State/outbox rollback together; duplicate/out-of-order hints; no subscribers; dropped invalidation; relay outage without blocking LAN; subscription/read race; dirty resource refresh; no leakage through identifiers/pagination; expired page session; deduplication. |
| Commands | Lost response before/after domain commit; concurrent same-key requests; changed-body conflict; restart during async work; status read after revocation; obsolete approval revision/options; duplicate approval from web and phone; idempotent chat send/read markers. No duplicate effect or fabricated completion. |
| Settings/upgrade | Disabled default on existing/fresh installation; setup remains optional; retry/back/resume; OAuth-only service; absent mobile module; remote capability lies; exact allowed origins; private-network opt-in; no provider binding changes on mobile switch. |
| Lifecycle | Phone revoke at disconnected relay; owner disconnect; key loss; app reinstall; platform backup restores; HQ/relay database restore; delayed old-generation frames; managed-to-self-hosted switch with online/offline phones; pending cleanup remains visible. |
| Multi-user access | Every endpoint carries an access rule; member without a business row gets 404 for its routes and never sees its IDs in lists; level too low for a write; access removed mid-session; last administrator and last owner protected; invitation state table including concurrent accept and demoted inviter. |
| Installation linking | Device code expired, denied and replayed; ID token with wrong issuer or audience; crash between local commit and installation registration; private mode makes no outbound request; unlink keeps local sign-in. |
| Push | Wake for another route's handle rejected; muted category and quiet hours send nothing; rate limit; token deleted on revoke, sign-out and profile removal; relay or gateway outage does not delay the outbox; payload contains only token, category, collapse key and profile reference. |
| Entitlement/limits | Beta/paid/expired/suspended entitlement; tampered client flags; byte-accounting reconciliation; quota exhaustion during response; no new command accepted after suspension; unknown outcomes recover after reconnect; no silent charge/upgrade or local/self-hosted lockout. |

Existing suites to retain include `ConnectionServiceSelectionTests`,
`ConnectionServiceOwnerGatewayTests`, `ConnectionServiceOwnerPanelTests`,
`AuthenticationEndpointTests`, `ApplicationRealtimeEventTests` and
`ApplicationRealtimeHubTests`. Add focused mobile suites in both repositories; do not
count synthetic service tests as real provider or mobile hardware evidence.

## End-to-end release matrix

Run separate Headquarters, relay and app processes. Use a Windows Headquarters and a
Linux Headquarters with Android and iOS physical phones across both managed-staging
and self-hosted relay installations. Headquarters has no inbound port mapping; at least
one test uses carrier NAT/mobile data, with relay access only on TCP/443. An emulator
or phone on the same Wi-Fi does not prove remote connectivity.

1. Fresh install and upgraded install leave mobile off and local setup usable. Choose
   a relay, enroll it, pair the phone with local user confirmation and view authorized
   businesses/overview, boards, conversations and approval details.
2. Cause real business changes at Headquarters; phone refreshes from hints. Send a chat
   and answer one supported approval; verify one durable effect and normal audit identity.
3. Switch Wi-Fi to cellular; suspend/resume and force-close/relaunch the app; restart
   Headquarters and relay independently. Restore state and uncertain command receipts.
4. Revoke the phone or remove its business membership while connected. Confirm that
   subsequent data/events/actions stop without waiting for the relay to acknowledge.
5. Exhaust a quota and expire a managed entitlement. Verify clear recovery/upgrade or
   self-host options, continued local access and no misleading success indicators.
6. Switch to an independently installed self-hosted relay with no C-Sweet account.
   Recover an offline phone via a local connection descriptor; preserve Headquarters
   pin, device identity and all provider OAuth settings/cleanup obligations.
7. Exercise protected-storage failure and backup restore on disposable installations.
   Confirm revoked devices cannot silently regain access. Document recovery steps.

### Product release exercises

8. Use the same email at two OIDC issuers. Sign up with zero memberships, accept an invitation and reject access to an ungranted business. Link an existing Market customer and verify the same entitlements. Remove membership during an active read/write stream and during directory downtime.
9. With a public egress block in place from first launch, use an internal relay and internal DNS/TLS. Enroll, authenticate, approve, operate, revoke, replace a lost phone and recover. Repeat with local users and a company IdP. Retain network observations and reproducible internal delivery/recovery steps.
10. Run each enrollment policy, tighten policy or promote a user while pending, and replay a late approval after denial. Verify duplicate enrollment messages and relay outages cannot bypass HQ checks.
11. On both phone platforms run the [workflow matrix](workflow-parity.md) as an allowed actor and attempt the relevant denied scope. Include browser return to the wrong active enterprise, interrupted file transfers, stale decisions, keyboard/gesture Back, assistive technology, and restore of the last profile with its HQ offline.
12. Record combinations exercised for Windows/Linux Headquarters, Android/iOS, official staging/internal/VPN routes and supported identities. Run all matrix workflows on both phones; run the transport/restore matrix across host and relay modes. Private-only workflows use internal services; external provider authorization uses its own connected fixture. Attach passing, failed and pending cells before the P7 decision.

## Capacity and cost controls

Initial proposed beta limits are configuration defaults, not a capacity claim or final
commercial promise. Apply the same safety mechanisms to self-hosting; operators can
change their own limits. Managed entitlements can lower ceilings but cannot exceed
the process's global safety budget. Advertise effective limits to the app.

| Limit | Initial default |
| --- | --- |
| Headquarters route | One active connector; newer authorized generation fences old attachment. |
| Devices | Pilot hypothesis: five enrolled devices and three concurrent phones per Headquarters. Measure multi-user use, rejected admissions and file traffic before setting general-release limits. |
| Pairing | Three outstanding invitations per user; five-minute expiry; ten failed attempts per invitation then cancellation. Add source-IP admission rate limiting. |
| Frames | 16 KiB encrypted bytes per frame; maximum encoded hub message 32 KiB. |
| Buffers | At most 256 KiB queued per direction/channel; at most eight concurrent inner requests per phone. |
| Inner payload | Initial control catalog: maximum 256 KiB JSON and 100 records/page, fewer to fit bytes. Negotiate a separate bounded file-transfer capability for the workflows in Phases 5–6 and measure it before release. |
| Timeouts | 30-second handshake/request budget; ten seconds for a blocked frame write before closing channel. Async actions return durable operation IDs within the request budget. |
| Traffic | 10 GiB/month per managed Headquarters beta entitlement; no automatic overage charges. Count both relay outbound directions and recipient fanout, including framing overhead. |

Quota exhaustion blocks new work and warns the app before the ceiling. Reserve a
separate bounded control budget for status/close/revocation; never interrupt a database
transaction to claim cancellation. App treats lost results as uncertain. Already accepted
work completes at Headquarters; its receipt is recovered when connectivity resumes.
Admission and message rates must be operator-configurable and globally bounded to
protect unregistered connections as well as registered customers.

Use a single relay process/region for the bounded beta; acknowledge its outage boundary.
The relay stores enrollment, invitation admission, entitlement and usage metadata, not
business history or offline commands. Reserve crash-safe usage budgets before forwarding
and reconcile actual bytes, so restarts cannot reset allowance. Store no plaintext
payloads; disable sensitive request logging and bound metric labels/cardinality. Initial
metadata retention: expired invitation/admission material 24 hours; content-free security
audit and daily usage aggregates 90 days. Keep current grants while active and revoked
grant fences until their credentials expire; quotas must survive restarts. Document these
periods in the service policy before launch and test pruning without reviving authority.

Capacity evidence must report concurrent HQ connections and phones separately, active
message rate, average/p95 payload size, fanout and outbound bytes, CPU, working set,
GC, queue depth, handshake time, rejection counts and p95/p99 end-to-end latency.
Exercise 100/500/1,000 Headquarters at one phone each, an eight-hour 500-HQ soak,
three-phone-per-HQ bursts and a full reconnect storm on a recorded server size.
Start with 1 KiB change hints every 30 seconds per HQ and one 32 KiB read/minute/phone;
then increase rates until the first bottleneck. This synthetic mix is a test input,
not a prediction of real customer use.

Pilot target on the selected size: p95 small-read latency under one second and p99
under three seconds, excluding Headquarters domain/provider work; no cross-tenant
delivery, unbounded memory growth or duplicate effects. Publish a managed enrollment
cap no greater than half the largest tested sustainable connection/traffic load with
at least 30% memory headroom. Adjust initial synthetic targets if evidence requires
it and record why before expanding enrollment. Large files, video and token streaming
require separate measured designs rather than silently sharing this budget.

Budget from measured compute, egress, durable storage, monitoring, support and redundancy.
Do not promise a per-server user count or monthly retail price in this plan. Show beta
terms before managed enrollment. A future paid tier is enforced at the service against
the enrolled Headquarters/account binding, never by trusting an app subscription flag.
Billing webhooks must be authenticated, deduplicated and reconciled against current
provider state before enforcement. Payment-provider integration, taxes and app-store
purchase policy review belong to the later paid-launch increment.

## Deployment and operator handoff

Before M6 publish tested instructions beside the service's hosting docs for: reachable
VPS/domain/TLS, WebSocket proxy settings/timeouts, local operator bootstrap, mobile-only
module startup without OAuth secrets, protected keys/database volumes, Headquarters
origin allowlisting, device enrollment/revocation, quota configuration, upgrade/rollback,
backup/restore and managed-to-self-hosted migration. Include health/readiness probes
that distinguish process health from a Headquarters route being connected. No LAN or
Office port is published by these instructions.

Alert on unavailable routes/handshake errors, queue/connection pressure, traffic budgets,
failed admission/revocation reconciliation and storage errors. Log fixed codes and
correlation IDs; redact QR content, admission proofs, certificate private keys, cookies,
chat/approval bodies and inner payloads. Reconnect uses exponential backoff with jitter
(one second to 60 seconds), cancellation and foreground recovery; wake hints cannot
bypass backoff. No tight reconnect loops or durable business-state polling at the relay.

Deployment requires the normal explicit release/deployment authorization. A docs change,
local package or passing test is not permission to change DNS, provision paid resources,
submit app-store builds, enroll real phones/accounts or alter production databases.
Keep the mobile capability disabled until the release gates pass; rollback disables
new sessions, preserves durable command receipts/revocations and leaves local access
available. Distributed multi-relay routing and high availability need their own acceptance
increments before being advertised. Push notifications are in scope; see the
[push guide](push-notifications.md).
