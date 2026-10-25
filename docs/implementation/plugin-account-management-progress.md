# Enterprise plugin account implementation progress

Updated: 2026-10-24. Scope: [approved plan](plugin-account-management-plan.md).

## Implemented in source

**Install → review abilities → approve enterprise installation → plugin page → Connect account →
native scope/privacy review → provider-hosted consent → authenticated import → business access.**

- SDK 5.5.0/protocol 2.4 remains the public account/resource/management contract. The host renders
  declarations without branching on Google/YouTube identifiers, scopes or channel-specific UI.
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
- Connector 0.2.0 declares Google identity, scopes, channel pagination, labels, permission groups
  and lifecycle requirements. Reserved partner consent is not offered. Agent 0.4.0 consumes this
  connector and SDK 5.5.0 via NuGet and asks for explicitly shared business channel access.
- Source migration `20261024215723_AddPerAccountOAuthSetup` adds independent setup/operator/
  revocation records, refresh fencing and unique verified account identity. **Not applied.**
  Existing supported legacy workloads and unrelated concurrent worktree edits were preserved.

## Verification

- Final selected host suite: **398 passed**, including actual enterprise installation, OAuth,
  identity isolation, partial consent, import recovery, binding/plan enforcement, refresh/profile
  changes, revocation, migration/model consistency and connector/media/approval/UI regressions.
- SDK/WorkManagement sibling references were disabled and isolated build directories used.
  Other existing sibling dependencies were not all disabled.
- Connector: **136 tests passed**, executable self-test passed, local 0.2.0 package produced.
- Agent: **200 tests passed**, executable self-test passed, local 0.4.0 package produced.
- Native Chromium acceptance passed install review/routing, scope/privacy review, provider redirect,
  paginated import, selected and whole-account grants/future acknowledgement, progressive reconnect,
  access removal, 390px layout and disconnect impact/pending deletion. Screenshots were inspected;
  native-card CSS isolation was corrected. No browser page errors were observed.
- Browser harness hosts real Razor components and account services with **synthetic provider
  responses**. Package preview/build/install is **simulated**. It does not prove production HTTP
  authentication, GitHub compilation, PostgreSQL multi-process concurrency or Google acceptance.
  Actual installation and exact agent-binding/plan enforcement have separate service tests.
- Prior SDK increment passed 593 SDK, 2 sample and 7 generated-template tests; those historical
  counts are not a claim that the SDK suite was rerun for this host-only account increment.

## Remaining release gates and code work

1. Complete provenance-aware provider-data deletion across inventory, inboxes, drafts, reports,
   conversations, processors and backups; verified deadlines/completion and periodic account/resource
   lifecycle checks. Disconnect intentionally remains visibly pending.
2. Native administrator assignment/revocation of connection operators. Persisted enforcement exists;
   the operator-assignment UI/API is not implemented. Administrators can connect accounts; ordinary
   business membership alone cannot.
3. Finish administration/recovery polish: build retry/update/remove controls on the full management
   page, cancelled-attempt housekeeping, administrative grouping when identity is unavailable,
   detailed grant-impact review and durable consumer notifications.
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
