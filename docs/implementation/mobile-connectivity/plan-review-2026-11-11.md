# Mobile plan review · 2026-11-11

Review of the handbook in this folder against the C-Sweet source at `1e31cffa` (working tree). The ConnectionService and Market checkouts were connected after the first pass and then checked: ConnectionService is at commit `a9fcb6c5`, declares 0.7.0, exposes `IConnectionModule`, and its startup registers OAuth only, as the handbook says; Market uses cookie Identity and is not yet an OpenID Connect issuer.

**Verdict.** The security design of the relay is strong and the documents are honest about what exists. The plan is not yet ready for junior developers, for three reasons: it contains two unresolved architecture decisions that change most of the work, it carries far more scope than "mobile connectivity", and its issues are written at epic size without files, types, schemas or done criteria. Section 4 lists the decisions; [junior-backlog-phase-0-1.md](junior-backlog-phase-0-1.md) makes the first two phases startable now because they do not depend on those decisions.

## 1. What is good

- **Grounded in source.** Every existing symbol and test class the handbook names was found (about 45 checked, including `ConnectionServiceSelectionStore.Probe`, `AppRealtimeState`, `IAppRealtimeConnection`, `ScopedActionAuthorizationService` and all named test suites). "Proposed" and "existing" are kept apart.
- **Trust model.** Relay routing, managed entitlement, connector, phone admission and business session are separate authorities. The relay can delay or drop traffic but cannot read or authorize it. Revocation commits at Headquarters first and never waits for the relay.
- **No invented cryptography.** Inner channel is standard TLS with an SPKI pin set at enrollment, and M0 is a real stop gate before contracts are published.
- **Durable writes.** Operation key + request digest + expected revision, receipt committed in the same transaction as the effect, query-before-resend. This matches the repository rule that events are wake hints and effects are idempotent.
- **Restore and relay switching** are treated as design problems (binding generations, restore starts with mobile disabled), which most plans skip.
- **Mobile is kept apart from provider OAuth** settings, and the capability probe stays false until everything behind it exists.
- **Bounded everything.** Frame, buffer, payload, request and pairing limits are stated with numbers.

## 2. Architecture findings

Ordered by how much work they change.

### F1 · Headquarters has one human user today; the plan assumes members with per-business grants

Evidence:

- `AuthenticationService.RegisterCoreAsync` returns `registration_closed` once an initial administrator exists. There is no second human account path.
- `src/CSweet.Api/Program.cs` defines three policies (`PluginAdministration`, `SourceControlAdministration`, `HostAdministration`), each `RequireRole(Administrator)`, and a fallback policy of `RequireAuthenticatedUser()`.
- Endpoints such as `OrganizationUserEndpoints` take an `organizationId` from the route and do not check that the caller may see that organization. Of 73 endpoint files (about 660 `Map*` calls), about 30 mention `RequireAuthorization`.

Consequence: every "Member, view that organization" cell in the parity matrix describes authorization that does not exist. Adding a second human who must not see everything means an access check on every list, detail, command, event and receipt path. Phase 2 gives this one issue ("Establish the Headquarters member"). It is the largest single piece of work in the plan and it is a web-product change, not a mobile one.

Recommendation: split it out as its own plan ("multi-user Headquarters"), and make the first mobile release serve the existing administrator only. Pairing in architecture steps 1–7 already works for "a current authenticated, enabled user" and needs no membership model.

### F2 · The plan does not say how 70 existing pages reach Headquarters through the new channel

Evidence:

- `CSweet.UI` is HTTP-shaped: 26 typed API clients plus 84 `.razor` files that inject `HttpClient` directly. `MauiProgram` gives them one `HttpClient`.
- The architecture requires "only compiled, allowlisted adapters", forbids "arbitrary URLs, HTTP headers, methods", and tells screens to use a new `IMobileHeadquartersClient`.

Read literally, each of the 187 matrix rows needs a new catalog operation, a dispatcher adapter and a rewritten page data path, and the web and mobile copies must be kept in step forever. The alternative is to keep the pages unchanged and replace only the bottom of the HTTP stack on the phone: an `HttpMessageHandler` that serializes the request into the inner TLS stream, and a Headquarters-side dispatcher that runs it through the same endpoint pipeline as the web, authenticated as the device's user, and only for routes on an explicit allowlist.

| | A · Allowlisted HTTP tunnel | B · Bespoke operation catalog (as written) |
| --- | --- | --- |
| New code per workflow | One allowlist entry and a test | Operation contract, adapter, client method, page change |
| Authorization | Same endpoint pipeline as web, by construction | Must be re-proved per adapter; today the checks live in endpoints |
| Parity drift | New web features work once allowlisted | Every web change needs a mobile change |
| Attack surface | Wider; depends on allowlist discipline and on F1 being solved before non-admins exist | Narrow and explicit |
| Idempotency | Add an operation-key header honoured by a filter on mutating routes | Built into the envelope |

Recommendation: A, with the allowlist generated from endpoint metadata (for example a `MobileOperation` attribute on each mapped route) so "compiled and allowlisted" still holds. If B is kept, general-release parity should be dropped as a goal and the catalog limited to a short list of workflows.

### F3 · Scope: five products are bundled

Besides the relay and the app the plan adds a new OIDC issuer repository (`CSweet.Identity`), an enterprise directory service, Market account linking, a multi-user access model (F1), and managed entitlement and metering. Phase 4 ("first integrated product milestone") depends on all of Phases 1–3, so nothing integrated ships until an identity provider with email verification and recovery exists. The M0–M6 sequence in the README does not need any of that and reaches a read-only pilot at M3.

Recommendation: make M0–M3 with QR pairing by the existing administrator the first release. Move shared Identity, directory, Market linking and company OIDC to a later, separate plan. One enrollment policy (confirm at a trusted session) is enough for v1; three policies triple the pairing test matrix.

### F4 · Two numbering systems with different dependency orders

M0–M6 and Phases 0–7 (also written P1–P7) describe overlapping work. Under M numbering, pairing (M2) needs only the relay (M1). Under phase numbering, devices (Phase 3) need membership (Phase 2). A junior developer cannot tell which order is binding. Pick one. The backlog in this folder uses phase numbers and cites the M gate each issue contributes to.

### F5 · Transport: the right gate, but the spike needs named risks

`SslStream` over SignalR binary frames is a defensible choice. M0 should explicitly cover these, because each one has stopped similar designs:

- **Headquarters as TLS server on Windows.** `SslStream.AuthenticateAsServerAsync` with a certificate whose key was created in memory commonly fails on Windows until the certificate is round-tripped through a PFX or stored in a key container. Decide where the persistent key lives (DPAPI-protected PFX, certificate store) during the spike.
- **iOS.** Confirm which TLS versions the .NET 10 `SslStream` offers on iOS, that a client certificate with a keychain-held key works, and that the custom validation callback is honoured for a self-signed server certificate. "Prefer TLS 1.3" may not be achievable there; say what the minimum is.
- **Android.** Client certificate from an app-generated key, and whether the key can be non-exportable.
- **MessagePack hub protocol under iOS AOT and trimming.** If it fails, the JSON protocol base64-encodes bytes (about one third overhead) and the 16 KiB / 32 KiB limits need rechecking. A plain WebSocket endpoint instead of SignalR is also worth ten minutes of thought: the relay only forwards bytes.
- **Certificates need lifetimes.** The acceptance register tests an "expired device cert" but no validity period or renewal is defined for either the Headquarters or the phone certificate.

### F6 · Events

Sending only `{event id, type, resource reference, revision}` and re-reading is right. Note that `AppRealtimeState` also maintains a "communication perspective" on the hub (`RestoreCommunicationPerspectiveAsync`); the mobile lane needs an equivalent or chat will not behave like the web. Chat token streaming is declared out of budget in the operations register, yet W-04 and W-07 require chat parity. State what the phone shows while a turn is running.

## 3. Gaps

**Product**

- G1 · Push notifications are deferred. For an owner who signs off approvals from a phone, being told that an approval is waiting is the main reason to install the app. At minimum decide the design (content-free wake through APNs/FCM sent by the relay) so the protocol leaves room for it.
- G2 · No app lock. The plan accepts "an unlocked paired phone is inside the trust boundary". A biometric or device-credential prompt on resume, and before approvals, is cheap and expected.
- G3 · No responsive audit. The 70 pages were built for desktop. Phase 1 covers the shell only; nobody is assigned to check that boards, tables and dialogs are usable at 360 px.
- G4 · Localization. C-Sweet ships in ten languages (`SupportedCultures`), including right-to-left Arabic; new mobile strings and right-to-left layout are not mentioned in any phase.

**Engineering**

- G5 · No data model. There are no entity, table, index or migration definitions for device grants, pairing invitations, operation receipts, relay bindings or policy.
- G6 · No wire definitions. Invitation encoding, inner message envelope, error code list and hub method signatures are described in prose only.
- G7 · No state tables. Device request, device grant, invitation and relay binding lifecycles are prose; juniors need a table of state, event, guard, next state.
- G8 · Threat model is promised in M0 but there is no document or template.
- G9 · Testability. `tests/CSweet.UnitTests` references `CSweet.UI`, not `CSweet.Maui`, and cannot reference a MAUI head. The plan never says that shell and session logic must live in a plain `net10.0` library to be tested. The named future suites (`MobileShellTests` and others) would otherwise be impossible to write.
- G10 · No mobile CI. `.github/workflows` has no Android or iOS build, no signing, and no Mac runner.
- G11 · Store readiness. `ApplicationId` is still `com.companyname.csweet.maui`; the icon and splash are template defaults; `AndroidManifest.xml` has `allowBackup="true"`, which conflicts with the key-storage rules. Apple requires in-app account deletion when an app offers account creation, plus privacy and encryption declarations. None of this is in Phase 7.
- G12 · Version skew. The plan negotiates protocol versions but does not state the supported window between app, Headquarters and relay, or what the user sees when the app is too old.
- G13 · Machine-specific paths. Commands use `D:/Repositories/CSweet.ConnectionService` and `../CSweet.Market`. `Directory.Build.props` already has the `...RepositoryRoot` pattern for sibling repositories; use it.
- G14 · Diagnostics. No crash reporting or redacted log export from the phone; support for a pilot will need one.
- G15 · Deep links and OIDC return need a registered scheme or universal/app links per platform; not specified.

**Junior readiness**

- G16 · Issues are epics. Example: Phase 2 issue 3 is "Implement browser OIDC with PKCE, real verification/recovery email and registered return handling", which is weeks of senior work.
- G17 · No issue names the files to create or change, the public types, or a definition of done.
- G18 · No sizes and no marking of which issues are safe for a junior and which need a senior owner (all cryptographic, authorization and migration work does).
- G19 · The prose is very compressed. Sentences such as "Recheck effective privileges, policy revision, identity, expiry and revocation at activation" are checklists for someone who already knows the design.
- G20 · The parity matrix has 187 rows, each needing evidence on two phone platforms. There is no priority order within a phase.

## 4. Decisions needed before Phases 2–7 can be made junior-ready

> Update 2026-11-12: an independent review corrected several statements in this document and the guides written after it; see the decision log. Counts here (26 typed clients, 84 pages) were re-measured as 15 client registrations and 88 pages.
>
> Update 2026-11-11: D1, D3 and D5 are decided (multi-user now, shared sign-in and Market now with the hosting choice at install, push from the start). The recommendations in this table for those three were not taken. Current status is in the [decision log](decisions.md).

| # | Decision | Recommendation |
| --- | --- | --- |
| D1 | Who can use the app in v1: the existing administrator only, or invited members? | Administrator only. Multi-user Headquarters becomes its own plan (F1). |
| D2 | How pages reach Headquarters: allowlisted HTTP tunnel or bespoke catalog? | Allowlisted HTTP tunnel (F2). |
| D3 | Is shared Identity, directory and Market linking part of this plan? | No. QR/private pairing first; shared sign-in later (F3). |
| D4 | Is "full parity" the release bar? | With D2 = tunnel, yes but prioritized: approvals, messages, overview, boards first. With D2 = catalog, no. |
| D5 | Push notifications in v1? | Design now, build after the read-only pilot (G1). |
| D6 | One numbering scheme. | Phases, with M gates as named checkpoints inside them (F4). |

Once D1–D3 are answered, Phases 2–6 should be rewritten to the same issue format as the Phase 0–1 backlog, with the data model, wire definitions and state tables (G5–G7) added to the architecture guide.
