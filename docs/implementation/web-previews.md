# Web Previews and independent WebHost

Status: implementation in progress, 2026-09-10. **No hosted preview is available end to end yet.**

## Accepted design

- Optional Web Previews plugin and separately installed CSweet.WebHost.
- Office retains agent execution; product processes receive no agent/MCP identity.
- Docker-packaged multi-service products run inside disposable certified VMs, never Headquarters' Docker daemon.
- Static widgets and container previews are team-only; public/production hosting is excluded.
- Standing project grants bound resources, concurrency, lifetime, source access and external connections.
  Application service count is constrained by available resources rather than an arbitrary fixed maximum.
- Diagnostics feed an assigned triage agent and deduplicated tickets under existing work permissions.
- Defaults: two concurrent previews, 2 vCPU/4 GiB/10 GiB each, two-hour lease,
  30-minute idle timeout and seven-day diagnostic retention. Ticket evidence must survive teardown.
  Native idle expiry and local diagnostic retention are implemented; Headquarters ticket delivery remains pending.

## Repository boundaries

Four repositories were initialized locally, with no remote repositories or releases created:

- CSweet.WebHost: product policy, privileged service, product guest, outbound Node and future gateway.
- CSweet.WebHost.Contracts: cross-service messages and agent-facing requests.
- CSweet.Plugins.WebPreviews: optional validation plugin and consuming-agent client.
- CSweet.Isolation: shared authorization, ISO and Hyper-V/guest socket implementations.

Headquarters owns current employee/workstream authority, business approvals, host registrations,
eventual distributed admission, delivery history and work-board integration.

## Implemented

- Strict manifests and constrained Compose normalization.
- Local atomic policy admission, idempotency and CPU-time reservations.
- Signed assignments plus separately signed short-lived control commands and durable replay ledgers.
- Hyper-V product provider, protected VM identity/recovery records and two read-only optical inputs.
- Windows RuntimeHost service code with SID-restricted pipe, independent lease/idle expiry and diagnostic retention workers.
- Windows ownership/write checks on protected paths and their parent replacement permissions.
- Product release certification verifies the exact image and runtime binary/dependency file set.
- Protected streaming artifact ingestion with an exact authorized digest, byte limit and separate cache capacity.
  Node cannot supply host paths, scripts or extraction directives.
- Dedicated Linux product guest: safe ZIP extraction, static serving, offline image import/build,
  constrained Compose execution, health checks, bounded HTTP and sanitized diagnostic capture.
- Host diagnostic ingestion binds canonical preview/project/build/revision identities.
- Signed evidence pagination works independently of a live VM; seven-day retention cannot be extended by replay.
  Expired evidence is excluded on reads and removed by an independent hourly worker.
- Identical container-log polling snapshots no longer create repeated events.
- Shared Windows VM and host/Linux guest socket helpers extracted from Office behind compatibility wrappers.
- Office Contracts 0.5.1 pins synchronized in Office and Headquarters; Office source version 0.1.1.
- Headquarters persistent grant/job/host models and additive migrations (scaffolded, not applied).
- Agent grant request and preflight handlers check current installation, optional provider and Workstream access.
- Exact grant proposals use the existing Approvals inbox, with a readable resource/access review.
- Human-owner approval rechecks current authority and unchanged terms; owners can revoke standing access.
- Owner-managed independent WebHost enrollment, inventory and revocation.
- Outbound-only Node HTTPS heartbeats with separate P-256 identity, exact audience/body binding,
  durable sequence reservation, bounded messages and replay protection across Headquarters replicas.
- Host identity/configuration permission checks, SYSTEM-owned runtime pipe verification,
  TLS certificate validation and disabled redirects, cookies, default credentials and proxies.
- Host-reported certification does not grant execution readiness.
- Typed consuming-agent grant/preflight client. The plugin validator itself always returns authorityGranted=false.

Grant request/preflight tools currently require declaration in the consuming agent's manifest and a normal
installation capability grant. First-party agent manifests have not yet been changed. Runtime start/read/stop,
test and renew handlers are not registered. Preflight explicitly returns RuntimeUnavailable until dispatch exists.
See the WebHost repository's docs/enrollment.md for implemented enrollment and evidence request flows.

## Remaining implementation

1. Build/install the product image and complete hardened real-VM certification. Provisioning inputs exist;
   no certified image, WebHost installation or real product VM has been created.
   Complete physical disk accounting for OS differencing files and per-instance media overhead,
   protected artifact cache cleanup and crash recovery before certifying the provider.
2. Connect authenticated Node command delivery, signing authority, independently verified provider certification,
   source/build artifact provenance and the protected local artifact transfer.
3. Transactional Headquarters quota admission, scheduling and existing build/preview record integration.
4. Lifecycle reconciliation, renewal and revocation delivery across disconnect/restart.
   Native expiry is implemented; Headquarters still needs to observe and confirm physical teardown.
5. Private gateway, isolated browser origins, access sessions and live membership checks.
   Guest HTTP is currently bounded to 4 MiB; streaming/ranges, cookies needed by product apps and WebSockets remain.
6. Browser tests, automatic diagnostic collection, Headquarters evidence ingestion, triage-agent assignment
   and deduplicated tickets with copied evidence. Local retained evidence alone does not provide this workflow.
7. Plugin setup/widgets and consuming-agent capability manifests; real static/game/server/database acceptance runs.

The old development-only LocalWebPreviewWorker remains unchanged until a working replacement exists.

## Verification

- WebHost: 55 behavioral/security tests passed using rebuilt packaged dependencies.
- Headquarters: API/AgentHost/UI build and 22 selected host/grant/approval/delivery tests passed with WebHost,
  Office Contracts and Isolation sibling references disabled, including competing Headquarters contexts.
- Office: 139 regression tests previously passed with Office/Isolation sibling references disabled.
  No additional Office source changes were made in this continuation.
- Office Contracts: 12 tests previously passed, including the captured 0.5.0 signature encoding vector.
- Updated RuntimeHost service built with packaged dependencies; plugin validation self-test passed.
- Twenty-four unpublished NuGet packages were built into .tmp/webhost-verified-packages.
  Every package version and relevant dependency pin was inspected. New packages are 0.1.0,
  Office packages 0.1.1 and Office.Contracts 0.5.1.
- Independent restores use .tmp/webhost-verification.nuget.config with explicit source mapping
  and fresh .tmp/webhost-verification-cache-v2 to prevent older unpublished versions masking changes.
  Headquarters and WebHost assets confirm package-only changed dependency boundaries.
- Migration model consistency passed. AddWebHostRegistrations SQL was generated and reviewed; not applied.
- Existing unrelated build warnings remain in the SDK Git build-task dependency and Communications.razor.
- No service installation, release signing, package publication, remote repository creation or live deployment occurred.

Office contributor instructions prohibit release signing/publishing from an ordinary development runner.
Real certification/signing must use the hardened platform workflows. This constraint does not replace
the remaining implementation work above.
