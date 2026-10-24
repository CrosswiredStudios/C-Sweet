# Enterprise plugin account implementation progress

Updated: 2026-10-24. Scope: [approved plan](plugin-account-management-plan.md).

## Implemented in this increment

- SDK 5.5.0: provider-neutral protocol 2.4 `accounts` declarations, fixed native layout,
  resource discovery/pagination, identity projections, permission groups, public policy links,
  and generic lifecycle intervals. Shared validation rejects unsafe/undeclared operations,
  executable extensions, invalid scope mappings, unbounded continuation inputs and old protocols.
- SDK schema, package metadata, documentation, security guidance, template defaults and tests
  are synchronized. Host central SDK pin is 5.5.0.
- Host DTOs mirror the declarations without Google/YouTube-specific application logic.
- Additive enterprise account-group/resource/resource-authorization/business-grant entities,
  revision/concurrency controls and consumer-binding references. Legacy connection uniqueness
  remains enforced for connections outside account groups.
- Source-only migration `20261024072327_AddEnterprisePluginAccounts`; **not applied**.
  Existing unrelated narrative migrations and worktree changes were preserved.
- The normal request-plan broker validates explicit consumer/resource/authorization/business-grant
  relationships. Plans freeze every independent authority revision and business-grant payload hash.
  Current authority is checked for execution and result retrieval. Standing-policy fingerprints
  include enterprise account authority, preventing policies from silently surviving changed grants.
- Generic complete-inventory discovery collector: bounded pages/resources, exact typed inputs,
  cancellation, duplicate-field rejection, identical overlap reconciliation and conflicting/repeated
  continuation rejection. Partial collections cannot be returned as a successful inventory.
- Plugin installation UI reads runtime admission policy before enabling Install, explains disabled
  imports and links to settings without changing policy. Successful installation opens its existing
  management section. This is **not** the complete new account-management page.

## Release gates still closed

Enterprise-account connector installation is explicitly rejected in `AgentInstallationService`
until the remaining end-to-end enforcement is deployed. Manifest preview recognizes validated
2.4 account declarations, not arbitrary unsupported 2.4 workloads. Existing 2.0–2.3 workloads
retain their supported behavior. Do not remove this gate merely to demonstrate a button.

No Google account was connected. No production verification/audit, real-provider acceptance,
package publication, database update or deployment occurred.

The YouTube plugin and agent repositories have **not** yet been upgraded in this increment.
Their existing releases are not evidence that the new enterprise account flow works.

## Remaining implementation order

1. Persist per-account setup/OAuth attempts independently of installation setup. Add authenticated
   administrator grant editing and business-filtered resource/consumer selection, with explicit
   whole-account future-resource acknowledgement and revision checks.
2. Complete enterprise OAuth: reviewed profile/digest binding, human consent/privacy acceptance,
   verified subject grouping or explicitly administrative grouping, partial/incremental scopes,
   same-principal refresh-token preservation, distributed refresh, and account-local recovery.
3. Wire authenticated discovery to resource import/reconciliation. Enforce account context on
   bootstrap, raw broker routes, every mutation/media/resume path and all result delivery.
4. Implement generic enterprise plugin Manage page and resumable Connect Account flow,
   account/resource grants, impact previews, local business revocation and durable disconnect/purge.
5. Upgrade YouTube connector to 0.2.0/current SDK and the agent to 0.4.0; add reviewed declarations,
   provider-specific setup, resource discovery and narrower business/agent binding behavior.
6. Finish browser acceptance, Google deployment/operator setup documentation and real-provider
   G1–G12 evidence. Existing dependency vulnerability warnings also need deployment review.

## Verification

- SDK solution: 593 SDK tests and 2 sample tests passed after contract changes.
- Authoring template: 7 generated-agent tests plus executable self-test passed using a fresh
  package cache and NuGet dependencies, not sibling SDK references.
- SDK 5.5.0 `.nupkg`/`.snupkg` produced in the task's `artifacts/feed`; NuGet ID/version verified.
  An isolated package probe compiled and exercised the public account validator.
- Final expanded host suite: 395 tests passed with SDK/WorkManagement sibling references disabled
  and a clean SDK verification cache. Coverage includes connector plans/actions/media/policies,
  enterprise account authority, discovery, migration consistency, imports, OAuth, setup endpoints,
  plugin UI and provider administration. An endpoint service-inference regression found by this
  expanded run was fixed and the entire selected suite rerun successfully.
- Migration tests generate PostgreSQL SQL and compare the current model/snapshot without opening
  a database. No browser or real-provider acceptance is claimed.
