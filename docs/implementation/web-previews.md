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
- Office Contracts 0.6.1 pins synchronized in Office and Headquarters; Office source version 0.1.1.
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
   Physical reservations now cover OS growth and media overhead, with conservative free-volume admission.
   Live allocation enforcement and complete VM crash recovery still need certification. Cache cleanup is implemented.
2. Connect authenticated Node command delivery, signing authority, independently verified provider certification,
   source/build artifact provenance and the protected local artifact transfer.
3. Transactional Headquarters quota admission, scheduling and existing build/preview record integration.
4. Lifecycle reconciliation, renewal and revocation delivery across disconnect/restart.
   Native expiry is implemented; Headquarters still needs to observe and confirm physical teardown.
5. Private gateway, isolated browser origins, access sessions and live membership checks.
   Guest HTTP is currently bounded to 4 MiB; streaming/ranges, cookies needed by product apps and WebSockets remain.
6. Browser tests, lossless/final diagnostic capture, Headquarters evidence ingestion, triage-agent assignment
   and deduplicated tickets with copied evidence. Local retained evidence alone does not provide this workflow.
7. Plugin setup/widgets and consuming-agent capability manifests; real static/game/server/database acceptance runs.

The old development-only LocalWebPreviewWorker remains unchanged until a working replacement exists.

## Verification

- Full Headquarters solution builds with ordinary defaults and sibling checkouts.
- WebHost: 70 behavioral/security tests; Headquarters: 33 focused host/grant/artifact/bundle tests in the final package run.
- Office: 139 regression tests; Office.Contracts: 13 tests, including the captured 0.5.0 signature encoding
  vector and the upstream certificate-recovery contract tests.
- Office, Headquarters and WebHost checks also passed with the changed sibling references disabled.
- RuntimeHost built against packages; plugin validation self-test passed with packaged WebHost and SDK dependencies.
- Twenty-four unpublished NuGet packages were built into .tmp/webhost-clone-packages.
  Package versions and Office.Contracts dependency pins were inspected: new packages 0.1.0,
  Office packages 0.1.1 and Office.Contracts 0.6.1.
- Package-only checks used .tmp/webhost-clone-verification.nuget.config and a fresh
  .tmp/webhost-clone-verification-cache; the final runtime continuation used a fresh
  .tmp/webhost-runtime-verification-cache. Source detection, explicit false overrides and missing-sibling
  fallback were verified separately.
- Migration model consistency passed; no database migration was applied.
- Existing unrelated SDK Git build-task and Communications.razor warnings remain.
- No service installation, release signing, package publication, remote repository creation or live deployment occurred.

Office contributor instructions prohibit release signing/publishing from an ordinary development runner.
Real certification/signing must use the hardened platform workflows. This constraint does not replace
the remaining implementation work above.

## Developer workflow and artifact preparation

- Clone-based builds now automatically detect sibling Isolation, WebHost, WebHost.Contracts and Office.Contracts.
  The Web Previews plugin also detects Agent SDK source. Explicit false flags still force package-only verification.
  Custom repository-root properties are used consistently by detection and project references.
  See [developer setup](../web-previews-development.md).
- The Office.Contracts checkout was fast-forwarded to upstream 0.6.0 certificate recovery while preserving
  the hosting changes. The compatible local maintenance version is 0.6.1; Office and Headquarters pins match.
- Artifact ingestion now persists the longest authorized retention lease before exposing new media.
  Repeated uploads verify their bytes without another disk copy. An independent worker removes expired
  leased media and interrupted generated temporary files; unknown or corrupt metadata fails closed.
  Cache cleanup uses a separate lock and cannot hold the VM expiry lock.
- Headquarters can prepare deterministic static product ZIPs from successful, completed, ingested builds.
  Preparation validates organization/project/repository/source identity, verifies the whole source archive
  and each output file, excludes private provenance, rejects guest-unsafe paths, and rechecks current build
  records and repository availability before returning the candidate. It runs no product code.
  This is an internal preparation service awaiting the authorized dispatcher, not a new agent execution endpoint.
- Unavailable hosts can report a missing image without being treated as certified or execution-ready.

Still required: certified physical disk enforcement and installation, signed dispatch,
transactional admission, build execution, private gateway, Headquarters evidence ingestion and ticket routing.
No hosting deployment or service installation occurred.

## Runtime storage and automatic diagnostic continuation

- VM admission now persists a conservative physical storage reservation covering the OS virtual disk,
  scratch virtual disk, artifact/boot media, VM memory state and overhead. It also checks fixed-volume
  free space while reserving the full cache allowance and a host floor. Heartbeats use these reservations.
- The same protected local volume is required for state and cache. Older active records with no physical
  accounting fail admission closed. Interrupted Creating records are cleaned up on the next reaper sweep.
  These changes do not establish filesystem quotas or replace real-VM certification.
- An independent RuntimeHost worker polls owned Ready/Failed guests, at most two at once, every 30 seconds.
  Polls revalidate signed ownership and expire after ten seconds without extending preview idle time.
- Guest snapshots now persist atomically with canonical identity binding and replay deduplication.
  Retrieval failures attempt bounded, deduplicated host evidence without raw transport errors.
- Collection and storage behavior add ten tests (70 WebHost tests total). Best-effort local polling does
  not yet provide lossless telemetry, Headquarters ingestion or finding-to-agent-to-ticket delivery.
