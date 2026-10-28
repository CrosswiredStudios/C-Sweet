# Enterprise plugin account implementation progress

Updated: 2026-10-27 (direct base-access connection and connector release checks). Scope: [approved plan](plugin-account-management-plan.md).
See [lifecycle implementation and explicit remaining boundaries](plugin-account-lifecycle.md).

## Implemented in source

- `AgentBuildService.ProcessNextAsync` verifies queued connector declarations before checking fleet
  availability. `VerifyConnectorDeclarations` revalidates the reviewed manifest's SHA-256, immutable
  repository/commit, package identity, capability descriptors and closed broker operation contracts.
  The resulting `connector-declarations.v1` digest identifies declarations, not a compiled or signed
  executable. No clone, compiler, builder VM, runtime settings or execution-pool allocation is used.
  Existing queued connector installs recover through the normal background-worker/retry path.
  Executable agents/services retain their isolated build path. OAuth client-profile, package review,
  account consent and per-business/per-agent grant checks remain separate and enforced.
- `PluginManage`, `PluginAdministrationPanel` and `PluginUpdatePanel` distinguish connector
  verification from compilation, show retry/diagnostics controls and explain worker readiness.
  Verification alone does not establish a working Google application or provider consent.

**Install → review abilities → approve enterprise installation → plugin page → Connect account →
provider-hosted consent → authenticated import → business access.**

The current purpose, included access and policy links appear inline before the initiating click.
The initial click acknowledges that exact package/policy and requests only required scope sets.
Reconnect/change-permissions retains explicit optional-access review. The provider still owns
actual account consent; no business grant or agent binding is created by clicking Connect.
See [plugin preview verification](youtube-plugin-preview-verification-2026-10-27.md) for delivery
changes, current checks and the remaining live-provider prerequisites.

- SDK 5.5.0/protocol 2.4 remains the public account/resource/management contract. The host renders
  declarations without branching on Google/YouTube identifiers, scopes or channel-specific UI.
- Plugin discovery now reads `CSweet.UI/Catalog/plugins.json` as a bundled resource, with local
  brand artwork through `PluginCatalogIcon`. The YouTube listing uses the current
  `CrosswiredStudios/CSweet.Plugins.Platform.YouTube` repository. Catalog edits require a UI release;
  they are not live settings or grants. See the catalog README.
- Service/connector installation is enabled by default, independent of the imported-agent and
  community-agent always-on opt-ins. Existing agent settings are unchanged. Package review,
  administration authorization, grants, resource limits and provider consent still apply.
  The obsolete link from plugin availability to agent-runtime settings has been removed.
- Broker-only connector correction (2026-10-25): `AgentInstallationService` ignores legacy VM
  request fields for connector packages, stores zero runtime memory/CPU/duration and no schedule,
  and does not require seeded runtime settings. Install/update approval, grants and configuration
  review remain unchanged. Update staging/approval, build retry and pause/resume accept no schedule.
  Runtime admission, automatic scheduling and manual runtime controls reject connectors. Executable
  agents/services retain their resource checks. No YouTube-specific runtime exception was added.
- `AgentInstallationService` accepts enterprise account connectors, rejects business scope and a
  second active installation. Zero connected accounts is valid. `Plugins` routes to `PluginManage`,
  explains build/profile readiness, and separates installation from external consent. Business
  selection and technical provider configuration are no longer ordinary install-review steps.
- `PluginAccountService` persists independent human-bound setup attempts. OAuth uses PKCE,
  protected single-use state, exact deployment HTTPS callback, package/profile/policy binding,
  database concurrency checks and platform-vault credentials. Optional partial consent remains
  unavailable; missing required consent blocks import. Expired sign-in verifiers are cleaned up.
- `PluginAccountProfileGuard` fingerprints actual deployment client metadata and secret, not a
  claimed profile name. Current persisted administrator/operator authority and lockout are checked.
- `PluginAccountTokenBroker` uses database refresh leases and credential generations, rechecks the
  reviewed profile after refresh, and blocks on scope loss. Recovery is account-local. Same-subject/
  profile reconnect can preserve an omitted refresh token; changed identity cannot retarget access.
- `PluginAccountService.Discovery` performs only reviewed bootstrap reads, verifies immutable
  subject, checkpoints each page, resumes interrupted imports and exposes only complete inventory.
  Same-looking labels never merge principals. Reconnect invalidates old authority/standing policies.
- `PluginAccountService.Grants` separates provider consent, business grants and agent binding.
  Selected resources remain explicit; whole-account sharing requires future-import acknowledgement.
  Manager views hide other resources/authorizations. Changes fence account concurrency and revoke
  affected bindings/policies, not shared provider credentials.
- `PluginAccountService.Bindings`, `ConnectorBindingService.Accounts` and native `PluginSetup`
  expose business-granted resources and bind the agent's exact resource/authorization. Existing
  broker checks freeze/revalidate independent authority, ownership, media and approval context.
- `PluginManage` includes empty state, privacy/scope review, per-authorization resource/permission
  health, reconnect, import retry, resource search, selected/whole-account business access, removal
  confirmation and disconnect impact. Ordinary account owners never enter technical credentials.
- Disconnect stops local external authority immediately and queues durable provider revocation.
  Broker-readable credentials are quarantined/retired. Deletion is explicitly **pending**, not
  claimed complete merely because a job was queued.
- `PluginAccountService.Operators` and native administration controls assign/revoke actual human
  connection operators for selected accounts or explicitly acknowledged all-account authority.
  Assignments bind the reviewed package and matching deployment profile; they do not grant business
  or agent access. Revoking an operator cancels its unfinished setup without revoking shared tokens.
- `PluginAccountService.Management` implements reviewed pause/resume/removal. Pending plans and
  autonomous policies are invalidated; resume never restores them. Removal stays pending until
  revocation and every recorded deletion obligation are confirmed, then retires the installation.
- `PluginUpdatePanel` exposes build diagnostics/retry, repository update preview/build, and separate
  exact-digest approval. The previous revision must finish account cleanup before replacement;
  credentials, grants and policies never transfer. No plugin-executable UI is loaded.
- `PluginAccountService.Lifecycle` schedules durable system-owned discovery, resumes interrupted
  pages, verifies unchanged identity, removes missing access only after complete scans and expires
  stale resource display metadata. Unchanged checks do not invalidate existing plan authority.
- `CSweetDbContext.ProviderData` records conservative account provenance for supported generated
  state, drafts, reports, messages, agent-authored tasks, work and diagnostics. Authorized native
  responses referencing known recorded IDs propagate provenance, not provider grants, to consumers.
- `PluginAccountService.Deletion` purges supported primary records, inventory and setup snapshots;
  retains content-free receipts; distinguishes primary cleanup from independent external cleanup;
  and shows overdue or protected-record blockers. Protected work/chat/model tombstones still require
  integration with the existing reviewed-erasure closure: those guards were **not** weakened.
- `IPluginAccountDataDeletionParticipant` admits only trusted deployment-owned confirmation adapters.
  The runtime adapter verifies workload destruction through the broker. A restricted retained-
  evidence adapter, native memory-confirmation adapter and signed backup/processor transport are
  present. Missing deployment cleanup configuration is diagnostic, not an install/connect/import
  gate. Physical external erasure still requires deployment services; missing proof remains pending.
- Connector 0.2.0 declares Google identity, scopes, channel pagination, labels, permission groups
  and lifecycle requirements. Reserved partner consent is not offered. Agent 0.4.0 consumes this
  connector and SDK 5.5.0 via NuGet and asks for explicitly shared business channel access.
- Source migration `20261024215723_AddPerAccountOAuthSetup` adds independent setup/operator/
  revocation records, refresh fencing and unique verified account identity. **Not applied.**
  Existing supported legacy workloads and unrelated concurrent worktree edits were preserved.
- Source migration `20261025004315_AddPluginAccountLifecycleEnforcement` adds deletion/provenance/
  administration records, system maintenance attempts, operator concurrency and recheck scheduling.
  **Not applied.** Rollback refuses to invent human ownership for existing system maintenance rows.
- Protected-erasure increment: `PluginRetainedEvidenceDeletionParticipant` uses the existing
  PostgreSQL work-erasure writer for exact recorded operational scope. Private/native context,
  unknown closure expansion, holds/review requirements and legacy content-bearing audit stay
  blocked. Original eligible human reviewers receive the existing native erasure-impact control.
- `PluginAuditRetentionGuard` verifies immutable audit omission instead of changing ledger rows.
  Provenance now runs before audit capture, so supported future provider-derived records/models
  omit content before entering the immutable store. Captured messages wait for native erasure
  evidence rather than being scrubbed before their retention review.
- `PluginSignedCleanupParticipant` provides backup/processor confirmation through deployment-owned
  mTLS endpoints and pinned public receipt keys. Scope/revision/hash/expiry/signature checks,
  bounded responses, persisted retry fences and append-only signed receipts fail closed.
  Physical external erasure services are deployment-owned and are not supplied.
- Source migration `20261025040252_AddPluginCleanupConfirmations` adds signed receipts, retry
  concurrency and append-only database guards. **Not applied.** No package sources changed.
- `PluginMemoryDeletionParticipant` confirms native receipts, captured-source fences, cleared input
  queues, protected turn tombstones and runtime resets without impersonating a human or bypassing
  holds. Generic memory/capture writes now retain content-free source lineage; new erasure receipts
  identify erased descendants. Existing native review controls are available to original eligible
  scoped operators as well as administrators. Unknown historical/direct-store lineage remains work.
- Removed the blanket cleanup-readiness gate. Production account consent/import succeeds with
  missing adapters; independent deletion completion still requires real evidence.

## Verification

- Broker-only connector increment: **501 focused tests passed**. The exact local YouTube 0.2.0
  manifest is embedded as a regression fixture (SHA-256
  `DCD537620260D60D8B236B76F141BD3D2F705D0F9BD784C662DAF369CFFF08DD`). Tests install it with
  VM/schedule request properties omitted, including with no runtime-settings row; verify zero
  VM grants and no schedule/runtime, and preserve executable-agent resource checks.
  Browser acceptance uses the production installer for that manifest and reaches the YouTube
  management page. Package verification is synthetic; the fake provider profile intentionally does
  not configure real Google consent. The separate synthetic account-to-disconnect story also passed.
  No real release installation, Google sign-in, database migration or product restart was performed.
- Catalog/default-availability increment: **106 focused tests passed** covering discovery/rendering,
  correct YouTube repository review, plugin install/re-enable with both agent opt-ins disabled,
  retained agent restrictions, installation-policy authorization and provider administration.
  Synthetic browser checks passed the local SVG, desktop/mobile catalog, correct repository,
  required approval and full install-to-disconnect story. No running product instance was restarted.
- Native memory-confirmation increment: **467 passed, 1 opt-in PostgreSQL theory skipped** in the
  focused selection. Wider adjacent selection: **1,202 passed, 493 skipped, 3 failed**, the same
  previously documented memory expectations and isolated-output root assumption. Not a green
  full suite or proof of PostgreSQL execution. The production API build passed.
- Browser install-to-disconnect/removal passed with Production fixture settings, missing cleanup
  services, non-blocking consent, a signed synthetic backup receipt and the real native-memory
  confirmation participant reading a synthetic erasure receipt. Missing proof stayed pending.
  This does not execute actual PostgreSQL memory erasure or contact Google/physical backup services.
- Latest protected-erasure selection: **452 passed, 1 opt-in theory skipped** (PostgreSQL cases).
  Production API build passed. Browser install-to-disconnect/removal passed and now exercises the
  production signed-receipt verifier/persistence with a synthetic gateway/key, rather than a plain
  boolean backup acknowledgement. No real Google or external storage service was contacted.
- Latest wider adjacent run: **1,188 passed, 493 skipped, 3 failed**: the same two previously
  reproduced memory expectations and isolated-output repository-root assumption described below.
  The wider suite is not green; skipped PostgreSQL tests are not acceptance evidence.
- Final lifecycle/administration selection: **437 passed**, including missing-credential revocation,
  protected-primary blocking, unknown-store blocking, provenance propagation, metadata expiry,
  system discovery identity/recovery, scoped operators, update digest/cleanup gates and legacy
  enable/disable/remove bypass rejection. Production API build succeeded with packaged SDK and
  WorkManagement contracts. No SDK/plugin/agent package source changed in this host-only increment.
- Wider adjacent selection: **1,173 passed, 492 skipped, 3 failed**. Two memory-capture expectation
  failures (`kind`, `nonhuman`) reproduce with the untouched prior `host-accounts` test binary.
  The third test assumes build output lives underneath the repository and cannot find its source
  root with isolated artifacts. These were not suppressed, and the wider suite is **not green**.
  Skipped opt-in tests are not evidence of PostgreSQL concurrency/real-provider acceptance.
- Prior account increment: **398 passed**, including actual enterprise installation, OAuth,
  identity isolation, partial consent, import recovery, binding/plan enforcement, refresh/profile
  changes, revocation, migration/model consistency and connector/media/approval/UI regressions.
- SDK/WorkManagement sibling references were disabled and isolated build directories used.
  Other existing sibling dependencies were not all disabled.
- Prior connector verification: **136 tests passed**, executable self-test passed, local 0.2.0 package produced.
- Prior agent verification: **200 tests passed**, executable self-test passed, local 0.4.0 package produced.
- Native Chromium acceptance passed install review/routing, scope/privacy review, provider redirect,
  paginated import, selected and whole-account grants/future acknowledgement, progressive reconnect,
  access removal, 390px layout and disconnect impact/pending deletion. Screenshots were inspected;
  native-card CSS isolation was corrected. No browser page errors were observed.
- Lifecycle increment also exercises scoped operator assignment/revocation, pause/resume, build
  diagnostics/no-update feedback, pending cleanup, reviewed removal and synthetic deletion receipts.
  Advanced operator controls are collapsed; disconnecting resources no longer show erased IDs.
- Browser harness hosts real Razor components and account services with **synthetic provider
  responses**. Package preview/build/install is **simulated**. It does not prove production HTTP
  authentication, GitHub compilation, PostgreSQL multi-process concurrency or Google acceptance.
  Actual installation and exact agent-binding/plan enforcement have separate service tests.
- Prior SDK increment passed 593 SDK, 2 sample and 7 generated-template tests; those historical
  counts are not a claim that the SDK suite was rerun for this host-only account increment.

## Remaining implementation and production validation

1. Complete private/native memory and chat cleanup coordination beyond the new restricted work
   adapter and original-reviewer controls. Legacy content-bearing immutable audit needs an explicit
   retention/erasure design, not a bypass. Complete provenance across legacy data, asynchronous input lineage,
   memory/export paths and other unclassified stores. Current capture is conservative, not proof of
   universal coverage. A blocked record must not be marked deleted.
2. Implement deployment-specific physical backup/processor erasure services; configure/test the
   mTLS signed-receipt adapter against them. Native memory confirmation is now implemented for
   recorded sources. Missing confirmation stays pending without gating onboarding. The browser gateway
   uses synthetic data/key/transport and is not proof of production storage deletion.
3. Finish durable consumer/admin attention notifications, complete account-identity retention
   handling after repeated failed refresh, installer retry idempotency, and recovery polish for
   updates needing configuration/direct-network review. Core operator/build/pause/remove controls
   are now implemented; their presence does not close these remaining boundaries.
4. PostgreSQL multi-process refresh/import/install race acceptance, installer retry idempotency and
   the real authenticated browser journey including agent selection and an exact approved action.
5. Publish real operator policies on the verified deployment domain and review matching declarations.
   Repository development notices are not production policies. Register the Google web client,
   enable APIs and satisfy applicable branding/scope verification, quotas and YouTube audits.
6. Authorized real-Google acceptance: consent/partial consent, verified identity, additional Brand
   Account authorization, actual accessible channels, refresh, revocation effects and verified purge.
   Follow the connector's `GOOGLE_SETUP.md`. No real Google account was connected.
7. Review existing high/moderate vulnerability warnings for SignalR.Common, cryptography packages
   and Microsoft.Build.Tasks.Git before deployment. Unrelated shared dependencies were not upgraded.

No package publication, production deployment, application restart, real-account mutation or database
migration application occurred. Broader unfinished content/live/partner features remain separate;
the new account-management page does not make the entire original YouTube feature plan complete.
