# Generic compute migration

Analysis date: 2026-09-11. This map was written before implementation.

## Decision and scope

WebHost is a discarded proof of concept. Remove its privileged hosting architecture and private-preview product integration; do not rename it wholesale, preserve its APIs indefinitely, or translate its grants into broader compute authority. Reuse independently useful isolation mechanisms. Build generic compute through the existing grant, broker, execution, audit and event conventions.

WebHost registration represents a **provider node**, not a VM. A preview job represents a **workload** and currently owns its VM assignment. Neither can simply be renamed ComputeEnvironment. The replacement must distinguish node identity, logical environment, provider instance, lease, operation and workload.

No runtime or database has been contacted by this analysis. Repository removal does not imply that installed services or existing VMs have been decommissioned. Historical migrations and releases remain historical records.

## Current execution path

Agent capability -> WebPreviewCapabilityHandler -> WebPreviewGrantService -> verified DeliveryBuild / execution artifact -> WebPreviewExecutionService admission transaction -> signed product assignment and WebHostCommands -> authenticated NodeDispatchLoop -> ACL-protected RuntimeHost named pipe -> independent signature/certification verification -> HyperVProductVmProvider -> NIC-less Linux product guest -> Docker/static workload and browser checks.

Browser access follows a separate human membership check, one-use ticket, preview-specific cookie and wildcard preview origin. HTTP and WebSocket traffic are brokered through signed guest controls, not public VM ports. Diagnostic evidence flows back into findings, assigned QA work and ordinary board-authorized tickets. Semantic preview changes create AgentPlatformEventOutbox entries inside SaveChanges.

## Component migration map

Paths are relative to the named repository. File families include all partial implementations.

| Component | Current responsibility and dependencies | Category | Destination / disposition |
|---|---|---|---|
| `CSweet.Isolation/src/CSweet.Isolation.Security/WorkloadAuthorizationEnvelope.cs` | Purpose-separated signed payload encoding and digest validation | KEEP | Shared authorization utility; new compute purpose, never accept old product signatures as compute authority |
| `CSweet.Isolation/src/CSweet.Isolation.HyperV/*` | Parameterized Hyper-V process calls and host/guest socket transports; also used by Office/SatelliteOffice wrappers | KEEP | Provider implementation utilities; never expose RunAsync or host paths to agents |
| `CSweet.Isolation/src/CSweet.Isolation.Artifacts/SingleFileIso9660.cs` | Immutable artifact media generation and verification; Office consumers | KEEP | Shared artifact transport |
| `CSweet.WebHost/...Core/AssignmentVerifier.cs`, `AssignmentLedger.cs`, `ProductControlVerifier.cs`, `WebHostIdentity.cs` | Signature, enrollment, sequence, claim and control replay checks; product contracts and durable state | EXTRACT | Generic signed compute authorization and fencing, with dedicated protocol purpose and independent privileged verification |
| `CSweet.WebHost/...Core/DurableState.cs` | Protected transactional local ledger, results, replay and stop history | EXTRACT | Provider operation journal and tombstones; split preview evidence from compute metadata |
| `CSweet.WebHost/...Core/ProductRuntime.cs`, `ProductRuntimeProtocol.cs`, `ProductGuestProtocol.cs` | Product provider interface and runtime/guest transport coupled to preview specifications | REWRITE | IComputeProvider plus bounded command transport; no mandatory build, manifest, Docker or browser fields |
| `CSweet.WebHost/...Core/ProductReleaseCertification.cs`, `ProductReleasePayloadVerifier.cs`, `Runtime.HyperV/ProductCertification.cs` | Trusted release/image/runtime-file certification | EXTRACT | Centrally managed template/provider certification; preserve immutable digests and trusted signing roots |
| `CSweet.WebHost/...Runtime.HyperV/HyperVProductVmProvider.cs` | Physical quota admission, VM shell, disks/media, start, protected metadata, local expiry | EXTRACT | Hyper-V compute implementation; separate guest setup and template selection from lifecycle |
| `HyperVProductVmProvider.Storage.cs`, `ProductStorageBudget.cs`, `WindowsProtectedPaths.cs` | Physical disk reservation, ACL/reparse-point protection | EXTRACT | Provider storage and path boundary; preserve physical accounting, not just logical scratch size |
| `HyperVProductVmProvider.Reconciliation.cs`, `.Status.cs`, `.Renewal.cs` | Stop tombstones, durable status, inventory and lease renewal | REWRITE | Desired state and observed state, generation fencing, leases and persistent/ephemeral policy; stop must no longer mean destroy |
| `HyperVProductVmProvider.Artifacts.cs`, `ProductArtifactCache.cs`, `Core/ProductArtifact.cs` | Verified media/cache and archive safety | EXTRACT | Generic artifact ingress; retain traversal/symlink/digest and byte-limit protections |
| `HyperVProductVmProvider.Guest.cs`, `.Diagnostics.cs`, `Core/ProductDiagnosticCollector.cs`, `Diagnostics.cs` | Guest exchange, resource/diagnostic collection, retention | EXTRACT | Bounded execution and observability; remove preview source/build assumptions |
| `CSweet.WebHost/...RuntimeHost/Program.cs`, retention/collection workers | Privileged Windows service, exact installer-owned config, ACL pipe, independent reaper | REWRITE | Generic compute runtime service; cleanup must work while HQ, agents and unprivileged node are unavailable |
| `CSweet.WebHost/...Node/*` | Unprivileged node enrollment/signing, heartbeat, durable command results and delivery | REWRITE | Compute node transport with bounded discovery and durable outcomes; preserve distinct node identity |
| `CSweet.WebHost/scripts/Install-WebHost.ps1` | Installs dedicated services, trust roots and protected paths | REWRITE | Compute installation/removal; old service cleanup is an explicit deployment step |
| `CSweet.WebHost/...Core/PreviewPolicy.cs`, `PreviewAdmission.cs`, `ManifestValidator.cs`, `ComposeNormalizer.cs`, `BrowserTestPolicy.cs` | Preview/Compose/build-specific validation | DELETE | Do not move into compute; a future web deployment plugin can implement a workload policy |
| `CSweet.WebHost/...ProductGuest/*`, `BrowserProbe/*`, `build/product-guest/*`, `examples/server/*` | Docker/static/browser guest, mandatory Docker service and Linux image | DELETE | Retire PoC guest. Future clean OS templates have no mandatory Docker/browser/bootstrap payload |
| `CSweet.WebHost.Contracts/.../PreviewContracts.cs`, `PreviewGrantContracts.cs`, `HostRegistrationContracts.cs`, `ProductControlContracts.cs`, `DiagnosticExportContracts.cs` | Public PoC protocol, identity, grant and preview DTOs | DELETE | Retire after consumers removed; introduce compute contracts without hosting names or compatibility authority |
| `csweet/src/CSweet.Domain/Setup/WebHostRegistration.cs` | Registered organization/provider node, identity, capacity, heartbeat and revision | DELETE | New compute node record; no automatic copy of PoC enrollment or trust |
| `WebHostDispatch.cs` | WebHostCommandRecord, project admission fence, preview evidence | DELETE | Compute operations/admission fence are new generic records; preview evidence retired |
| `WebPreviews.cs`, `WebPreviewFinding.cs`, `WebPreviewBrowserSession.cs` | Approved preview policy, jobs, findings, triage and browser sessions | DELETE | Remove PoC records; preserve already-created ordinary work tickets/artifacts |
| `Infrastructure/Setup/WebHostRegistryService.cs`, `WebHostDispatchAuthority.cs` | Owner registration, signed heartbeat, revocation, signing and trusted release selection | REWRITE | Generic compute-node registry and authorization; remove dependency on installed web-preview plugin |
| `Infrastructure/Setup/WebPreviewExecutionService*.cs` | Artifact-bound serializable admission, quota, dispatch/results, uncertain-outcome recovery, controls | REWRITE | ComputeBroker and reconciler. Reuse invariants, not product-domain code |
| `WebPreviewGrantService.cs` | Owner-reviewed exact artifact/proposal activation, actor/workstream checks, preview quota | DELETE | Extend ScopedActionGrant with structured constraints and existing approval pipeline; independent actions |
| `WebPreviewMaintenanceWorker.cs` | Queue reconciliation, expiry, evidence/session cleanup and QA dispatch | DELETE | Generic compute reconciliation and lease cleanup workers |
| `WebPreviewGatewayService*.cs`, `WebPreviewManagementService.cs`, `WebPreviewTriageService.cs` | Browser tickets, scoped transport, dashboard, QA findings and tickets | DELETE | Retire private-preview PoC; future publishing uses explicit network grants |
| `Infrastructure/WorkManagement/WebPreviewArtifactService.cs` | Deterministic preview packaging from successful delivery build and signed execution output | DELETE | Retain generic artifact store/provenance/build services; compute provisioning must not require an artifact |
| `Persistence/WebPreviewConfigurations.cs`, `CSweetDbContext.WebPreviews.cs` | Nine tables, concurrency/indexes, semantic notification capture | DELETE | Remove tables with new migration; reuse atomic outbox pattern for compute |
| `Api/Core/WebHostEndpoints.cs`, `WebHostDispatchEndpoints.cs` | Owner node API and signed host heartbeat/poll/result/artifact endpoints | DELETE | Versioned compute node API; no old-route compatibility required for discarded PoC |
| `Api/Core/WebPreview*Endpoints.cs`, `WebPreviewGatewayMiddleware*.cs` | Preview access, revocation, HTTP/range/WebSocket origin isolation | DELETE | Retire associated routes and registration |
| `UI/Pages/WebPreviews.razor`, `Components/WebPreviewGrantReview.razor`; preview branches in Approvals/NavMenu | Private preview dashboard and special grant approval UX | DELETE | Generic compute status/approval view later; retain general approvals and navigation |
| `AgentHost/Broker/WebPreview*Handler.cs`, `WebPreviewToolSchemas.cs`; MCP entries and Program registration | Agent tools and authorization adapters | DELETE | Generic compute/storage/network/DNS broker capabilities |
| `AgentPlatformEventDispatcher.cs`, `AgentPlatformEventOutboxItem` | General durable agent events with preview-specific retry exceptions | KEEP | Remove preview-only branches; use semantic compute events and bounded current-state recovery |
| `CSweet.Plugins.WebPreviews` | Optional preview plugin/client/callbacks | DELETE | Retire package consumption; no replacement privileged web plugin |
| `CSweet.Agent.SoftwareDeveloper` preview event/capability branches and plugin client reference | Preview management, missed-event discovery, notifications | DELETE | Remove manifest subscriptions/requirements and obsolete tests; bump synchronized agent version before release notes |
| `CSweet.Agent.Engineer.VideoGame` SpecialistAgent event override, GameEngineerExecution preview branch and client reference | Same private-preview callback/client as Software Developer | DELETE | Remove preview declarations and tests; keep ordinary game build/implementation flows and bump version |
| `CSweet.Agent.SoftwareQA` PreviewTriage partial, contract reference and capabilities | Reads canonical findings and files scoped tickets | DELETE | Retain ordinary QA/work-item features; bump synchronized version before release notes |
| `Directory.Build.props`, `Directory.Packages.props`, Infrastructure csproj and sibling props/csprojs | Local/package switches and PoC package pins | DELETE | Remove only retired dependencies; validate package mode when changing surviving packages |
| Existing `ExecutionFleet*`, `ExecutionWorkloadOrchestrator`, Office/SatelliteOffice runtime and build services | Employee/runtime/toolchain execution, enrollment, leases, artifacts, provider placement | KEEP | Existing execution plane; compute can share provider mechanics without granting workload guests employee broker credentials |
| `ScopedActionGrant`, `ScopedActionAuthorizationService`, installation grants | Scoped actions plus installation capability ceiling | REWRITE | Add constrained infrastructure actions; retain existing consumers and do not infer infrastructure grants from roles |
| `DataProtectionPluginSecretStore`, `OutboundNetworkPolicy`, connector/DNS/file-transfer infrastructure | Existing separately controlled credentials and external operations | KEEP | Compose via explicit grants; no credential copying into compute records |
| `LocalWebPreviewWorker/Server`, `WebPreviewBundle`, generic `PreviewSessions` | Separate development-only immutable static-build viewer using delivery contracts; does not depend on WebHost | KEEP | Artifact-viewing workflow, not a privileged compute provider. Revisit separately if all static preview UX is to be retired |
| ASP.NET `IWebHostEnvironment`, `ConfigureWebHost`, `builder.WebHost`, Namecheap `.web-hosting.com` | Framework APIs and DNS suffixes unrelated to PoC | KEEP | Never blanket-replace these text matches |

## Replacement model and boundary

Core domain: ComputeEnvironment (logical identity/owner/desired state), ComputeSpecification (OS, architecture, CPU, memory, disk, GPU, template, network request, persistence and lifetime), ComputeTemplate (operator-managed immutable reference and features), ComputeInstance (provider/node/resource ID and generation), ComputeLease, ComputeOperation, ComputeStorageAttachment and ComputeNetworkPolicy. Do not store provider credentials, host paths or arbitrary privileged scripts in these records.

Desired state is Running, Stopped or Destroyed. Observed lifecycle separately includes Requested, Authorizing, Provisioning, Bootstrapping, Ready, Busy, Stopping, Stopped, Destroying, Destroyed and Failed. Provider operations can remain asynchronous. Unsupported OS, snapshots, GPU, resize, storage or network features fail explicitly before provisioning; never silently weaken a request.

IComputeProvider owns provision/start/stop/restart/destroy/status and bounded guest execution. Optional snapshot, resize, attachment and network operations have explicit supported-capability checks. Provider inputs are broker-authorized immutable specifications and operation identities; agent DTOs cannot select host image paths, daemon endpoints or provider secrets. Templates may advertise Docker, but clean OS templates must not require it. Keep the privileged service separate from the unprivileged agent/node.

Persist the desired environment, immutable request digest, operation and notification outbox together. A unique organization + installation + idempotency key rejects changed terms and deduplicates identical requests. Logical environment identity prevents different triggers from provisioning the same desired environment under different request keys. Use transactional quota admission across all grants/nodes, concurrency tokens and provider-side stable identity/fencing. Persist intent before side effects; after uncertain outcomes reconcile by identity rather than launch again. Do not release quota on timeout or Failed until physical teardown is confirmed.

Office/provider changes advance placement generations only after fencing old execution and accounting for unresolved resources. Agent deletion requests cleanup for ephemeral resources; persistent infrastructure remains durable under organization ownership with access revoked and an explicit transfer/deletion decision. Local lease enforcement must survive control-plane outages; persistent infrastructure has authorization/renewal policy distinct from idle expiry.

## Permissions

Extend existing scoped action grants, rather than create WebHostingGrant or another unrelated approval system. Installation capabilities remain an upper bound. Infrastructure actions use one consistent namespace/version convention compatible with the broker: compute provision/read/list/start/stop/restart/destroy/snapshot/resize/execute/persist; storage create/attach/detach/persist/delete; network outbound/inbound/publish-port/create-private-network; DNS create/update/delete; secrets access remains separate.

Attach bounded, versioned policy constraints to scoped grants: CPU, memory, root/attached disk, GPU, concurrent environments, cumulative resource budget, OS/architecture/template allowlists, maximum lifetime, persistence, allowed network modes/ports and DNS zones. Evaluate the relevant action independently at admission and again at privileged dispatch. Provision permission never implies networking, persistent storage, arbitrary execution or secrets. Do not combine constraints from unrelated grants to synthesize broader authority. Persist all contributing grant IDs/revisions in audit records.

Default network is none. Model internal/private and outbound-only separately from inbound, published port and public endpoint/DNS. Persistent volumes outlive an environment unless separately authorized for deletion. Artifact references are distinct from disks. Provider credentials stay in platform-controlled secret storage. Guest workload secrets require their own current authorization and delivery channel.

## Ordered implementation and schema strategy

1. Complete this inventory and capture baseline checks. No broad deletion before this map.
2. Remove PoC integration from Core, API, UI, agents and package graph. Preserve shared execution/isolation/artifacts. Remove obsolete tests rather than adapting them to assert the retired behavior.
3. Add a forward EF migration removing PoC schema and cancelling only PoC pending proposals/events. Preserve historical approvals/artifacts and copied work tickets. No conversion of PoC grants or registrations into generic authority.
4. Decommission installed WebHost services/VMs before applying destructive schema cleanup to a live database. Retain exact host metadata until teardown is confirmed. Source changes alone cannot prove deployed cleanup. Do not uninstall or destroy live infrastructure during repository editing.
5. Add provider-agnostic domain and constrained scoped grants, with isolated policy tests. Add generic persistence and atomic outbox capture. Keep initial provider support explicit and fail closed.
6. Implement transactional broker admission, stable operations, current-state read/list, lifecycle reconciler and cleanup. Test concurrency/crash/uncertain outcome before enabling physical provisioning.
7. Extract privileged provider mechanisms with clean-template support; preserve signed boundaries, physical quotas and local recovery. Add shared provider contract tests and hardware acceptance for supported templates.
8. Add independent storage/network/DNS brokers and explicitly grant-controlled composition, then agent tools and user-facing compute status/approval UX. Unsupported providers/features remain unavailable, never a host-shell fallback.
9. Remove retired WebHost runtime/plugin/package publishing surfaces after reusable mechanisms are extracted. Update deployment documentation, package versions and release notes; verify published package consumption independently of sibling references.

PoC tables to drop, in FK-safe order: WebHostCommands, WebPreviewBrowserSessions, WebPreviewEvidence, WebPreviewFindings, WebPreviewProjectAdmissions, WebPreviewTriageRoutes, WebPreviewJobs, WebPreviewGrants, WebHostRegistrations. Keep the old AddWebPreviewGrants, AddWebHostRegistrations and AddPrivatePreviewDispatch migrations so existing databases can advance. Update the model snapshot through EF. Down cannot restore discarded PoC rows; document the one-way data cutover. Preserve unrelated PreviewSessions, DeliveryBuilds, ExecutionWorkloadAssignments and generic event/audit tables.

Replacement schema should have its own documented purpose: ComputeEnvironments, ComputeInstances/operations/leases/templates, scoped-grant constraint extension, independent storage/network records and attribution/audit. Unique provisioning keys and ownership indexes, revision concurrency tokens, expiry/reconciliation indexes and restrictive ownership relationships are mandatory. Creation alongside obsolete tables is only a staged deployment interval with the removal migration above, not indefinite duplicate infrastructure.

## API and agent changes

Remove `/api/core/organizations/{organizationId}/web-hosts`, `/api/web-host/v1/{heartbeat,poll,result,artifact}`, private `/web-previews` routes, preview grant revocation, preview middleware and its rate-limit registrations. Remove MCP web-preview tools and special approval dispatch. Retain normal authentication, antiforgery, rate limiting, approvals and artifact APIs.

Introduce organization-scoped compute environment request/read/list and lifecycle operations through authenticated broker context (installation identity comes from authentication, not caller-supplied ownership). Operations return durable IDs/state rather than await VM creation. Node APIs authenticate a separate enrolled provider identity and bind every result to operation, node, environment and generation. Expose a compute.changed event containing resource ID/revision only; authorized reads are authoritative. Networking/storage/DNS requests remain separate operations.

## Verification map

- Retire PoC-only WebHostRegistryTests, WebPreviewGrant/Dispatch/Gateway/Transport/Operation/Lifecycle/FindingTicket/Artifact tests with their implementation. Preserve LocalWebPreviewServer/Worker, WebPreviewBundle and ProjectPreviewLinks tests for the independent artifact viewer.
- Update AgentPlatformEventDispatcherTests, MCP catalog tests and shared integration fixture setup to remove preview-specific expectations/services. Preserve unrelated framework WebHost usages.
- Remove Developer/Game Engineer PreviewCallbackTests and QA PreviewTriageTests; update manifest capability/event expectations, package references and synchronized versions.
- New authorization tests: organization/installation/workstream/resource ownership, disabled/deleted actors, action and grant revocation, expiration, exact constraints, each network/storage/DNS permission independently, no secret or host-path authority.
- New relational tests: same-key/same-terms, same-key/different-terms, concurrent distinct keys for one desired environment, quota across grants/nodes, committed state+outbox rollback, crash before/after provider side effects, stale/out-of-order outcomes, app restart and bounded read/list recovery.
- Provider contract tests: idempotent provision/destroy, stop preserves disk, restart, missing/failed instance, retry with same identity, generation fencing, local TTL, artifact/command bounds, unsupported template/features, storage detach/preservation and explicit network configuration.
- Lifecycle tests: TTL cleanup, persistent idle preservation, agent deletion, grant/node revocation, Office migration, orphan reconciliation and audit attribution including rejected actions. Confirm quotas remain reserved for uncertain teardown.
- Migration tests: upgrade old schema with representative PoC and unrelated rows, remove only intended tables/pending actions/events, preserve execution/build/artifact/work-ticket data; fresh database model parity and no pending model changes.
- Build/test Core and affected agents, pack every changed surviving package and inspect nupkg version. Real VM isolation/network/storage tests are separate from unit success.

## Risks and functionality intentionally retired

Deleting WebHost removes private static/container deployment, wildcard origin access, range/WebSocket proxying, guest Chromium checks, standing preview approvals, in-place preview renewal, diagnostics retention and automatic QA finding-to-ticket triage. These are intentionally retired PoC features, not reasons to preserve the hosting abstraction. Existing normal builds and copied tickets must survive.

The most serious migration hazards are abandoning live VMs by deleting controller records, losing replay tombstones, releasing capacity while teardown is uncertain, accidentally inheriting broad permissions, and treating unsigned/self-reported provider status as certification. Preserve distinct trust roots and purpose-separated signatures. Old signed product commands must not become valid compute requests.

The shared Hyper-V wrapper currently hardcodes a Linux secure-boot template, two-disk topology, no NIC and disabled checkpoints; it is reusable infrastructure, not a complete generic provider. Clean Windows, GPU, snapshots, durable volumes, DNS and public networking are not implemented by the current PoC. Do not advertise them merely because the new domain can represent them.

Baseline focused test invocation (`dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --no-restore --filter 'FullyQualifiedName~WebPreview|FullyQualifiedName~WebHost'`) fails before tests: sibling WebHost.Core expects PreviewBrowserCheck and PreviewBrowserCheckResult missing from sibling WebHost.Contracts. Further source inspection also finds newer request/event DTO references absent from that contracts file. This is pre-existing contract drift, not a migration regression. Existing unrelated nullable warnings and a dependency advisory are also present.

## Progress

- [x] Repository analysis and component map, updated for explicit PoC retirement.
- [ ] Core/agent/API/UI PoC removal and schema retirement.
- [ ] Generic compute domain, grants, persistence and broker.
- [ ] Provider extraction and reconciliation.
- [ ] Independent infrastructure capabilities and compute UX.
- [ ] Migration, package and behavioral validation.
