# Focused plugin account setup

`PluginManage` renders native account setup from the existing
`PluginAccountManagementView` and `PluginAccountManagement` declarations. It does
not inspect provider IDs, add an SDK layout version, or execute a plugin-defined
workflow. Existing `accounts-and-resources.v1` packages remain compatible.

## Presentation and authority

`PluginSetupPresentation.ForAccounts` projects typed Requirements, Business access
and Status sections. Business access is omitted for connection operators and
declarations without business permission groups. Section completion is a display
hint, never consent, an access grant, or a claim that an agent can publish.

The plugin supplies account/resource labels, scope-set purposes, permission groups,
policy links and consent purpose. The existing backend supplies connection
availability, authorized accounts/resources, saved attempts, revisions and grants.
Core owns the review controls, policy acceptance, provider redirect validation,
resource selection, permission checks and all mutations.

`OpenConnect` opens a review; it makes no credential or consent request. Required
scope sets are included, optional sets remain unselected for a new account, and
reconnect preserves the existing selection. Fresh-consent recovery keeps its
existing permission ceiling. `Connect` requires explicit policy acceptance and
sends the reviewed package digest/policy version. Unknown start outcomes require
a successful status refresh and fresh acceptance before another submission.

`PluginSetupAttempt` shows the current saved attempt. Previous attempts live in a
collapsed history with no resume actions, so an old failure does not obscure a
new success. Import resumes retain exact revision/receipt fencing and never retry
automatically. Account sign-in details, resources and destructive controls are
disclosures; business access remains an explicit separate action. Saved grants
show their declared allowed abilities. Agent hiring, resource binding and action
approval remain separate from account connection and business grants.

`OnParametersSetAsync`, navigation generations, route cancellation and sequenced
reads discard responses from older routes or refreshes. Setup-query and URI changes
invalidate same-installation reviews, including Back/Forward navigation. Location
changes immediately advance the action-response generation, even before router parameter
delivery or a return to the original URI. Reads use a separate route generation advanced
by parameter delivery and disposal, so a later location subscriber cannot discard the
new route's pending read. Consent responses also check the current URI before provider navigation, and disposal
invalidates outstanding response generations even when cancellation is ignored.
Package changes invalidate open reviews. Transport exception bodies are not displayed.

## Current coverage and adapter boundary

- Native account connectors: implemented, including the actual YouTube manifest
  fixture and an unrelated `example.workspace` connector. Both use the same
  sections and renderer. Optional abilities can be requested through the existing
  reconnect review; a read-only connection is not upload or publishing authority.
- Configuration-only and no-setup plugins: preserve their existing management
  routes, without synthetic OAuth or business-access steps.
- API-key/remote MCP accounts: preserve the existing native key-account page,
  credential isolation and separate consumer authorization. This change does not
  wrap that page in the focused account renderer.
- Agent dependencies: retain their own selection and binding flow. This change
  does not install, hire, bind or invoke an agent.

Future adapters can reuse `PluginSetupProgress` with their applicable typed
sections and their existing authorities. They need their own behavior and
security tests; this slice does not claim one backend covers every setup type.

## Verification

`PluginManageConnectionTests` covers review/back, consent gating, repeated clicks,
navigation during consent, out-of-order reads, package changes, multi-account scope
isolation, saved pending/expired/empty states, uncertain resume receipts, and safe
diagnostics. `PluginSetupPresentationTests` compares the actual YouTube declaration
with the synthetic workspace declaration and verifies optional sections.
`PluginSettingsTests` verifies other setup archetypes keep their existing routes.
The account-import CI workflow includes these regressions on Windows and Linux.

`CSweet.PluginAccount.AcceptanceHarness/browser_acceptance.py` exercises actual
components and account services with synthetic identity/resources only: explicit
review/back, provider consent, imports, scoped operators, selected/whole grants,
progressive permissions, mobile layout, revocation and cleanup. It does not
prove live Google authorization, video transfer, agent execution or publishing.
