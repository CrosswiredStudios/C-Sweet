# C-Sweet mobile product and implementation handbook

**Delivery status · documentation only.** Build an open-source Android and iOS client for a running Headquarters. On sign-in, a person opens their enterprise, chooses a business or a menu destination, and operates it remotely. General release requires functional parity with authorized workflows in the running Headquarters web app. Read-only and limited-write releases are pilots.

## Vocabulary

| Term | Meaning in the app |
| --- | --- |
| Enterprise | One Headquarters installation. Switching enterprise changes the server, credentials, business list and navigation history. |
| Business | One organization inside the active Headquarters. Selecting a business changes the focus of the business menu. |
| Account | C-Sweet account (the same record as a C-Sweet Market account), Headquarters-managed local user, or company OpenID Connect identity. Invitation acceptance links an identity to a Headquarters member. |
| Linked / Private | The hosting choice made at initial install. A linked Headquarters uses C-Sweet services (account, directory, official relay, push). A private one contacts no C-Sweet host. |
| Member | A person with an account at one Headquarters. An administrator can do everything; other members act only in the businesses they have been given access to. |
| Device grant | Headquarters authorization for one member and phone key. One phone can hold independent grants for several enterprises. |
| Relay | The open-source service that carries encrypted traffic. Both endpoints connect outward. An operator can use the official service or run the same service on an internal network or VPN. |

Users install and bootstrap Headquarters on its host. During that first run they choose **Connect to C-Sweet** or **Private Headquarters**; connecting creates the administrator's C-Sweet account as part of registration. Subsequent administration of a running Headquarters belongs in phone parity. Business state and execution stay at Headquarters. The phone packages a native shell (sign-in, pairing, enterprise switching, notifications) and loads each enterprise's pages from its Headquarters through the encrypted channel; it keeps one foreground enterprise active. This thin-shell approach is on trial, with bundled pages as the fallback.

## Reading and implementation order

Build order and how each step is tested: [increments](increments.md).

New here? Read the [decision log](decisions.md), then take an issue from the [Phase 0–1 backlog](junior-backlog-phase-0-1.md). The [plan review](plan-review-2026-11-11.md) explains why the decisions were needed.

Three guides hold the design and issue lists for work that spans phases:

| Guide | Covers | Runs during |
| --- | --- | --- |
| [Multi-user Headquarters](multi-user-headquarters.md) | Members, business access levels, endpoint enforcement, invitations, People & access | Phase 2; must finish before any non-administrator can sign in |
| [C-Sweet accounts and installation linking](identity-and-installation-linking.md) | Market as issuer, hosting choice at install, linked registration, directory | Phase 2 |
| [Request tunnel and thin shell](tunnel-and-thin-shell.md) | How pages reach Headquarters, route marking, local proxy, web-to-shell bridge, the phone trial and its thresholds | Foundation in Phases 3–4; trial at the Phase 4 milestone |
| [Push notifications](push-notifications.md) | Gateway, relay tokens, Headquarters notification decisions, phone handling | Spike in Phase 0, relay and phone in Phase 3, categories in Phase 5 |

Start with the [annotated screen designs](ui-design.md), then read [architecture and protocol](architecture.md). Find your workflow in the [parity matrix](workflow-parity.md) and open its phase below. The [acceptance and operations register](acceptance-and-operations.md) owns completion evidence for both the product and relay.

| Phase guide | Dependency | Demonstration |
| --- | --- | --- |
| [0 · Baseline and transport](phase-0-baseline.md) | Source and physical-device test environment | Trace workflows and prove the encrypted stream before publishing contracts. |
| [1 · Mobile shell](phase-1-shell.md) | Designs and startup trace; can proceed alongside the spike | Navigate launch and switching states with deterministic fixtures. |
| [2 · Accounts and membership](phase-2-accounts.md) | Authority and workflow inventory | Invite one person into one of two businesses using shared and private identity. |
| [3 · Relay and devices](phase-3-devices.md) | Validated transport and membership | Enroll and revoke physical phones under each policy. |
| [4 · Enterprise home and switching](phase-4-enterprise.md) | Shell, identity and device transport | Operate two real installations from one phone. First integrated product milestone. |
| [5 · Daily operations](phase-5-daily-work.md) | Live Enterprise and canonical reads | Complete and recover messages, decisions, work, calendar and document actions. |
| [6 · Administration and remaining parity](phase-6-administration.md) | Live sessions and durable writes | Operate remaining features, including files and provider callbacks. |
| [7 · Release and operations](phase-7-release.md) | Completed workflows and production-shaped installs | Verify parity, compatibility, accessibility and isolated private operation. |

## Taking an issue

Use the phase story to pick one demonstrable workflow. Put its matrix ID, repository/project, prerequisite and existing service symbol in the issue. Write one example input, expected state change and rejected case before touching code. Wire the normal entry point and service together, run the named focused tests and attach the before/after result. For a fixture, label the result as simulated. For a device run, record device, OS, server and relay versions. The [switching guide](phase-4-enterprise.md) works through a late-response bug from reproduction to handoff.

Commands assume PowerShell at the C-Sweet root, a sibling Market checkout and the verified ConnectionService checkout. Substitute its actual path on other machines and run iOS commands on the Mac build host. The implementation guides name future mobile test classes so the author knows where the regression belongs; add and discover those tests before claiming a pass. Record new Identity/directory project and verification commands in Phase 2 when their service layout exists.

## Verified baseline

| Existing component | Evidence and reuse boundary |
| --- | --- |
| Setup and Settings | `Setup.SetupSteps` contains optional `connection-service`; `ConnectionServiceSetupStep` is also used by `ManagedConnections`. Official/SelfHosted/Skip currently select the provider connection service. |
| Capability probe | `ConnectionServiceSelectionStore.Probe` pins origin/protocol and forces mobile pairing off. `ConnectionServiceSelectionTests.ProbePinsOriginAndDoesNotEnableUnimplementedFeatures` enforces this. |
| Provider pairing | `ManagedPluginConnectionGateway` and `ConnectionServiceOwnerGateway` implement customer/owner authority for provider connections. Neither enrolls a phone. |
| Realtime | `AppEventsHub`, `AppEventGroups`, `AppRealtimeEventEnvelope`, `ApplicationRealtimeOutboxDispatcher` and `AppRealtimeState` implement direct authenticated app events. They do not create an outbound relay. |
| App shell | `CSweet.Maui` targets Android/iOS and shares `CSweet.UI`; `MauiProgram.ApiBaseUrl` is hardcoded for local development. `AppRealtimeState` uses browser credentials and needs a native transport implementation. |
| Service | Inspected sibling `CSweet.ConnectionService.Core.IConnectionModule` supports independent statically registered modules; its inspected startup registers OAuth, without mobile relay support. |

Sources: [onboarding pilot](../connection-service-owner-pilot.md),
[provider connection plan](../managed-connection-service-plan.md),
[provider progress](../managed-connection-service-progress.md), and
[native communications](../native-communications-hub.md).

The protocol 0.7.0 package/source match is recorded in [architecture](architecture.md). Mobile capability remains disabled pending implementation and acceptance.

## Delivery defaults

The read-only pilot covers Enterprise, business overview, work, conversations, approvals and artifact metadata. The first write pilot adds send, mark read and revision-bound approval responses. Phases 5 and 6 expand to the full workflow matrix before general release, including administration, file transfers and provider callbacks.

Keep unfinished edits in memory. If a response is lost, recover its durable result at the originating Headquarters with the original operation key. Foreground realtime uses invalidations and bounded reads on resume. Push notifications are part of the first release and carry no business content; see the [push guide](push-notifications.md). Peer-to-peer paths and automatic LAN switching are separate enhancements. End-to-end encryption protects business content. Routing identifiers, timing and sizes remain visible to the relay.

Private deployment uses a reachable internal relay and locally trusted TLS on the company network or VPN. Identity, enrollment and recovery can operate entirely inside that network. A self-hosted relay requires no C-Sweet connectivity subscription.

## Delivery sequence

Each work package owns code, migrations, documentation and the matching acceptance
evidence. Names below are proposed symbols, not claims about existing implementation.

| ID | Deliverable and owner boundary | Depends on | Exit condition |
| --- | --- | --- | --- |
| M0 | Reconcile service/protocol source and packages; freeze mobile module contracts and threat model; validate the inner TLS/native transport spike. | None | Exact baseline recorded; Windows/Linux Headquarters and Android/iOS prove encrypted framing, key use, bounded buffering and disconnect behavior. Stop if the selected runtime cannot support it; revise the design before publishing contracts. |
| M1 | Add `CSweet.ConnectionService.Mobile` through `IConnectionModule`: relay enrollment, role-bound admission, bounded routing, usage counters and deployment artifact. | M0 | Two isolated Headquarters routes exchange opaque test traffic with their own clients only; standalone self-hosted setup requires no C-Sweet account or OAuth registration. |
| M2 | Add Headquarters mobile settings, protected identity, outbound connector, pairing invitation/device lifecycle and audit/migrations. | M1 | User confirms and revokes an enrolled device; service outages never block local setup; no inbound listener is added for relay transport. |
| M3 | Add native transport, logical Headquarters API adapter, authorized read catalog and event invalidations. | M2 | Physical Android and iPhone read authorized data and recover after network/Headquarters/relay restarts; read-only pilot is usable. |
| M4 | Add beta mutations and durable result recovery through canonical application services. | M3 | Lost responses/restarts/retries produce one chat message or decision; changed authority and stale decisions fail closed. |
| M5 | Complete managed beta metering/entitlements, relay switching, operating runbooks, capacity tests and security acceptance. | M4 | Both hosting modes pass the release matrix; documented outage, backup and quota exercises pass. |
| M6 | Release app/service/protocol and run a bounded free managed beta. | M5 | Published artifacts are installed normally; approved deployment and monitoring evidence exist. |

M0–M6 establish pilot connectivity. General release additionally requires Phase 7 parity evidence. Writing a contract, packing
a local package, testing fake providers or compiling the MAUI project is not a physical
phone or deployed relay acceptance result. Payment collection is a later commercial
increment; entitlement enforcement and a migration path must exist before beta launch.

## Integration and rollout rules

Keep mobile configuration separate from provider OAuth bindings. Extend the existing
Connections step with distinct **Provider accounts** and **Mobile access** sections;
mobile has its own Disabled/Managed/SelfHosted selection. A mobile relay change must
not retarget provider credentials or bypass provider cleanup restrictions. An explicit
reuse-current-service action may copy an origin, then probe and enroll the mobile
module independently. Do not silently enable mobile for existing installations. A new installation that chooses **Connect to C-Sweet** at first run has explicitly asked for it: its Mobile access starts as Managed and enrolls in the background without blocking setup.

Keep `ConnectionServiceProbe.MobilePairing` false until the local client, selected
service module, compatible protocol and enabled installation all support the feature.
Probe support is not enrollment, connectivity, user authority or entitlement. Expose
those states separately. Existing OAuth versions must continue to work without mobile.

The app and Headquarters live in C-Sweet. The relay and wire protocol belong to ConnectionService. Identity, directory and Market integration have separate owners described in the architecture guide. Use a
separate mobile protocol namespace in the existing BCL-only protocol package; a native
transport library contains SignalR/TLS dependencies. Reuse `CSweet.Contracts` business
DTOs rather than duplicating them into the service package. Do not involve agents,
SDK, Office or WorkManagement package changes unless an actual required API change
is demonstrated. Follow repository versioning rules for every package actually changed;
publish an additive protocol minor, synchronize consumers/templates and verify packed
metadata and package-only consumers. App and relay releases remain independently versioned.

The managed offering starts as explicitly labeled **free beta; paid plans planned**.
Do not promise permanent free service or a price based on unmeasured capacity.
Subscription loss affects managed connectivity only. Users retain local access and
can switch to a self-hosted relay through the mobile-specific flow.
