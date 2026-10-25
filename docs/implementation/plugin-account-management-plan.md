# Enterprise plugin accounts and resource access: implementation plan

Date: 2026-10-23  
Status: approved design, implementation in progress. See [current source, tests and remaining release gates](plugin-account-management-progress.md). This document records intent, not a claim that every acceptance criterion is complete.

## 1. Outcome and scope

Implement this normal user journey:

**Install → review plugin grants → Approve & install → plugin page → Connect Account → provider consent → import accessible resources → assign business access.**

Install an integration once for the enterprise. Maintain multiple account groups beneath that installation, with multiple provider resources beneath each account. A business may receive access to a whole account or selected resources. Its agents receive a subset of that business access.

For YouTube, the plugin supplies the labels Google account, channel, publishing and comments. C-Sweet implements provider-neutral installations, connections, resources, permissions, grants, approvals and native rendering. No host branch, page, database field or permission enum is selected by a YouTube/Google package ID.

This plan replaces the former business-scoped, one-channel-per-connector onboarding design. It covers installation, account setup, discovery, management and access enforcement. Completing unrelated YouTube content/live/partner features remains separate work.

Reviewed prototype: [account-first proposal](C:/Users/PC/.codex/visualizations/2026/08/04/019fcb97-68da-7d33-9074-4e48c7324811/youtube-account-setup.html).

## 2. User experience

### Install

1. Enter through the plugin catalog or the existing GitHub URL import option.
2. Select Install and show a native review with publisher, version, enterprise scope, requested platform abilities, provider destinations and links to full declarations. Distinguish installing available abilities from granting access to external accounts.
3. Approve & install approves the exact reviewed package and destinations, creates the enterprise installation, and queues its verified build. Retried submissions reuse the same reviewed import/install identity.
4. Land immediately on the plugin page. Display actual queued/building/failed/installed state; expose Connect Account when prerequisites are satisfied. Never describe a queued build as completed installation.
5. An installed plugin with zero connected accounts is a valid state. Show the primary Connect Account CTA and a short explanation, rather than marking the entire plugin as broken or requiring employee setup.

Global package-import policy remains enforced. If installation is disabled, explain that before approval and offer an administrator-only policy action. Never silently enable imports. Build and dependency failures remain on the plugin page with actionable recovery.

### Connect Account

1. The CTA starts the plugin-declared account flow. The host checks actor permissions, deployment readiness and the reviewed package/profile approval.
2. Redirect the browser to the declared provider authorization endpoint. For YouTube this is Google's hosted sign-in and consent page, including its account/channel selection and MFA where applicable. Do not embed the provider page in an iframe or ask C-Sweet users for Google passwords.
3. Request the plugin-declared scopes for the selected feature set. Start with base reads by default; request optional management or publishing scopes through an explicit feature selection or later Enable action. Google receives OAuth scopes, not internal business grants or action approvals.
4. Validate the callback and returned authorization, then return to the same plugin/account context without exposing codes or tokens in page state.
5. Run the plugin's discovery through the restricted broker. Persist verified resources page by page, show import progress, and expose imported resources without requiring users to enter IDs or repeat confirmation for every provider-verified record.
6. Open the account's Channels view for YouTube, with a business-access CTA. A connected account initially grants no business or agent access.

Cancellation returns to the plugin page and creates no usable partial connection. Handle partial consent, denied consent, no accessible resources, import interruption, expired authorization, duplicate connection and provider errors explicitly. Clean up orphaned authorization material through durable recovery.

### Manage

- The plugin page lists account groups with identity/display label, connection health, import status, resource counts and business-grant counts.
- Open an account to see plugin-labeled resource, business-access and permission views.
- Discover/import only resources available through verified authorization. Support pagination and a resumable import; a partial scan is never represented as the complete account.
- Whole-account grants cover current and future **authorized, imported** resources. Require explicit acknowledgment of future-resource inclusion when that scope is selected.
- Selected-resource grants cover only the immutable selected IDs. Discovering more resources does not expand them.
- Business users see only their permitted account projection and resources; do not leak other channel names, counts, ownership or activity.
- Keep deployment provider configuration in administrator-only Administration. Managed deployments supply it ahead of ordinary account setup. Self-hosted deployments configure it once. If missing, show an administrator setup task, without making ordinary users configure an OAuth application.

Account disconnect previews every affected business and agent. Revoking one business grant previews only that grant's consumers. Both disable unstarted external work immediately; neither promises to undo provider effects already sent.

## 3. Current baseline and version strategy

Inspected on 2026-10-23:

- The host supports System, Organization and User installation scope, but explicitly requires connectors to use Organization scope.
- `PluginConnection` has one external account and one `BoundResourceId`; dependency binding and execution require the connector and agent to share a business.
- `PluginOrganizationGrant` exists for system communication services. It is a coarse organization allowlist, not an account/resource/capability grant.
- Plugin manifests already declare providers, scopes, operations, safe setup flows and personal settings. The current Manage panel does not render those flows.
- The host pins SDK 5.4.0; SDK source is 5.4.1. YouTube plugin 0.1.0 references SDK 3.38.0; agent 0.3.1 references SDK 3.65.0.

First establish a passing compatibility baseline on the current SDK release line. Do not assume the previous 3.x builds or test counts prove compatibility with the current host.

Introduce additive account/resource contracts in SDK **5.5.0**, or the next available additive release if shared development has advanced it before implementation. Introduce host protocol **2.4** enforcement for enterprise account/resource authority; existing 2.0–2.3 workloads retain their existing supported behavior. Older hosts must reject manifests requiring the new semantics.

Target YouTube connector **0.2.0** and agent **0.4.0**, with an exact compatible dependency range `>=0.2.0 <0.3.0`. The pre-1.0 minor change advertises the revised connection contract. Update all affected package pins, templates, samples, documentation and package tests. Verify package-only consumers and pack metadata; sibling project references must not hide stale packages.

## 4. Ownership and generic boundaries

| Component | Responsibilities |
| --- | --- |
| C-Sweet host | Enterprise installation, trusted publisher/build review, native UI, OAuth mechanics, vault, durable state, business grants, agent bindings, request authorization, approvals, media transfer and audit |
| SDK | Typed provider-neutral declarations, validated management contributions, discovery contracts, account/resource references and consumer binding APIs |
| YouTube plugin | Public Google metadata, requested scope sets, account setup definitions, channel discovery/mapping/validation, YouTube operation semantics, permission labels and result reconciliation |
| YouTube agent | Conversation, objectives, business context, selecting an authorized channel, durable work, drafts and reporting through typed plugin operations |

Host code may understand an operation's declared effect, required scope set, resource type and input schema. It must not interpret a Google scope string as a built-in feature or contain `if YouTube` rendering/execution logic. Treat approved connector semantics as reviewed trusted code/declarations; UI sandboxing alone cannot prove their honesty.

## 5. Data model and access model

### Enterprise boundary

In the inspected application, System scope represents the current C-Sweet deployment/enterprise and Organization represents a business. Permit reviewed connectors at System scope without granting them ambient access to every business. Do not represent enterprise ownership using a fake business ID in resource authorization.

Propagate the actual deployment/tenant owner boundary through installation records, account records, vault namespaces and endpoints. System scope must never become authority across independent hosted customer tenants. Keep existing system communication-service authorization unchanged unless explicitly covered by regression tests.

### Separate presentation, credentials and resources

| Record | Purpose |
| --- | --- |
| Enterprise plugin installation | One approved integration build and management declaration per enterprise |
| Account group | User-facing provider account container; stable internal ID, verified identity when available, display metadata and health summary |
| Provider authorization | One independently consented credential set, exact provider/profile/subject context, granted scopes, revision, status and vault reference |
| External resource | Provider resource ID/type, owning account group, verified authorization relationships, display metadata and discovery status |
| Discovery run | Authorization context, durable cursor, observed records, completion status and reconciliation checkpoints |
| Business connection grant | Business, account group, whole-account or selected-resource scope, allowed operation capabilities, revision, grantor and revocation |
| Agent connection binding | Business/agent installation, dependency, allowed account/resource selection, subset of operations and exact package digest |
| Frozen action plan | Existing approval/execution plan extended with account, resource, authorization and grant revisions |

An account group is **not one token**. A channel may require a distinct provider authorization even when it is presented beneath the same account. Every read and mutation must resolve to a currently verified authorization for its exact resource.

Do not use email, display name or a plugin-supplied parent ID as proof that resources belong to the same Google account. Prefer verified provider identity. If Google does not expose a trustworthy parent identity for an authorization, an enterprise administrator explicitly associates it with an account group; display that association accurately and audit it. Account grouping never substitutes for resource authority.

Extend the existing organization-grant infrastructure where it fits, but retain account/resource/capability grants as explicit records. A coarse plugin organization allowlist must not authorize all connected accounts. Do not create two independent grant evaluators that disagree.

### Effective authority

Allow an operation only when all are valid:

1. Enterprise installation and reviewed package/profile approval.
2. Requesting business membership and business connection grant.
3. Agent installation/dependency binding and operation grant.
4. Exact target resource belongs to the approved account group and grant selection.
5. A live provider authorization supports that resource and required scopes.
6. An exact action decision or applicable standing policy authorizes a mutation.

Business connection grants confer no general cross-business data or conversation access. Agent bindings remain an independently approved subset. Whole-account business access does not silently expand an existing channel-bound agent or its autonomous policy.

### Who may perform each step

- Enterprise plugin administrators approve installation/build/profile access, configure deployment credentials, assign business access and review enterprise-wide disconnect impact.
- An explicitly authorized human account owner/connection operator completes provider consent and reconnects their authorized account context. Completing Google consent does not grant them enterprise administration or permission to assign arbitrary business access.
- Business managers select from granted resources and authorize agent subsets within their business. They may request additional account access, but cannot approve that request themselves without the corresponding enterprise authority.
- Agents may request declared operations and guide setup. They cannot complete human Google login, edit enterprise grants, reveal credentials or authorize their own public mutations.

## 6. Plugin-driven management contract

Extend the existing manifest/setup framework; do not add a parallel custom-page framework.

Add validated declarations for:

- Multiple account instances of a connection declaration.
- Account identity/display projection, using bounded fields from reviewed brokered provider responses.
- Resource collections: type ID, singular/plural label, display fields, discovery operation, pagination mapping and ownership-validation operation.
- Permission groups linking human-readable labels and scope sets to exact existing operation capabilities/effects.
- A `connection-manager` UI contribution selecting the connection declaration, account setup flow and resource collection.
- Capability-limited account bootstrap/discovery callbacks and their input/output schemas.

Use a bounded native `accounts-and-resources.v1` layout initially. It provides account lists, resource lists, safe forms/selectors, consent actions, status views, business-grant editors and disconnect impact controls. Labels such as Account, Channel and Publishing come from the plugin's reviewed declarations.

All state bindings use approved typed data sources and validated JSON pointers. Reject arbitrary HTTP data sources, executable expressions, remote UI, HTML, JavaScript, Razor components and iframes. A plugin cannot add a button that directly grants itself authority, reads vault values, chooses arbitrary redirect URLs or calls an undeclared operation.

Host rendering requests a management projection from authorized APIs; it never trusts client-supplied account/resource IDs or callback results as authorization. Escape external text and validate/proxy any external images before displaying them. The prototype's sample consent dialog is replaced by actual provider navigation.

## 7. OAuth and YouTube discovery

Retain host-owned authorization-code OAuth, PKCE S256, short-lived single-use bound state, exact redirects, encrypted credential storage, refresh serialization, revocation and redacted audit. Expand state binding to the enterprise installation, account-connection attempt, authorization slot, intended resource context, package digest, profile and initiating human.

Preserve administrator approval of the actual executable package/profile pairing. Combine package and profile-use review into the same initial administrator approval when a compatible deployment profile already exists; missing deployment configuration remains a separate administrator task. Do not weaken approval to matching publisher/profile names.

Plugin setup supplies the Google public metadata and base scope sets. Additional scopes remain user initiated. Google consent grants provider access; business grants and action approvals stay inside C-Sweet. Reconsent must not silently reactivate autonomous policies or replace another channel's authorization.

YouTube implementation:

- Discover through authenticated `channels.list` using supported filters. Implement provider pagination and expose import completeness accurately.
- Support account groups containing at least 100 channels without assuming one authorization or one API response covers them all. The host has generic configurable collection limits; YouTube-specific limits belong in plugin declarations.
- Verify ordinary Google/Brand Account behavior with authorized test accounts before promising automatic all-channel import. `managedByMe` / content-owner discovery is restricted to eligible partner/CMS authorization and must never be used as a general-account shortcut.
- Import every accessible channel under the appropriate verified authorization, deduplicating canonical channel IDs and retaining the authorization relationship. If another authorization exposes an existing channel, reuse its canonical resource record rather than duplicating operations or data.
- When an expected channel is unavailable, offer Authorize another channel through the same account page. Do not claim unavailable channels were imported or are writable.
- Account identity/email display requires verified provider evidence. Request additional identity scopes only if actually needed, declare and disclose them, and never infer identity from browser hints.
- Keep current progressive sets: base reads/analytics, publishing, content management, memberships and eligible partner operations. Show only abilities actually implemented and available.

Google supports managing up to 100 channels through one Google account, but that is not an API discovery guarantee. See [YouTube account switching](https://support.google.com/youtube/answer/3046356?hl=en) and [channels.list filters and pagination](https://developers.google.com/youtube/data/reference/rest/v3/channels/list). The browser consent flow follows [Google web-server OAuth guidance](https://developers.google.com/youtube/data/guides/auth/server-side-web-apps).

## 8. Execution, revocation and recovery

Update binding, bootstrap, reads, mutation execution, media transfer, result access and approval services together. Removing only the same-business comparison would create an authority gap.

Freeze enterprise installation, business, agent, account group, resource, authorization ID/revision, business grant/revision, agent binding/revision, package digest, canonical payload, media provenance, action revision and idempotency identity into each plan. Recheck authority before execution, credential injection, ownership reads, upload initiation/chunks and result delivery. Never let a model choose an unvalidated account/resource route.

Ownership checks use the selected resource and its verified authorization, not a caller-supplied channel ID beside arbitrary request data. Existing conditional-edit guards and uncertain-outcome reconciliation remain enforced. Package, account, scope or grant changes invalidate affected plans; reconnect never retargets a pending approval.

Revocation behavior:

- Revoke business grant: invalidate its agent bindings, standing policies and queued actions; keep other business grants and underlying provider authorizations intact.
- Remove one agent: remove its bindings/obligations without disconnecting the shared account.
- Disconnect account: disable all associated authorizations/resources immediately, invalidate dependent work, then run durable provider revocation and provenance-aware cleanup.
- Revoke one channel's business access: do not revoke a token shared with other authorized resources.
- Provider-side revocation may affect multiple credential sets. Track shared revocation domains and disable/revalidate all affected authorizations; verify actual Google behavior before claiming isolation between tokens.

Discovery reconciles absent resources only after a complete verified run; a partial or failed page is not evidence of deletion. Uncertain effects reconcile before retry; no blind replacement uploads/replies. Resumable setup/import work survives restarts and duplicate callbacks. Keep content-free audit records and track deletion of provider-derived copies across business inboxes, drafts, reports and conversations.

## 9. Agent integration

Adapt YouTube Manager to resolve its declared dependency to an explicitly authorized account/resource binding rather than a business-owned connector installation.

Keep a default channel for each agent installation/work context. When several channels are permitted, ask for the target once and persist the selection for the relevant task. Never choose the first discovered channel or change a target after approval. A grant to the whole account is not an instruction to publish to every channel.

Pass opaque account/resource binding references outside provider input payloads. Keep channel identity visible in human review cards while hiding credential and internal routing mechanics. Continue conversation and recovery assistance when access is lost, with external work blocked.

Reuse the typed client and current durable work/reconciliation paths. Do not rewrite unrelated agent features as part of onboarding. Requests from other agents inherit only their explicit business/agent grants, not the caller's presumed account ownership.

## 10. Delivery order

| Phase | Deliverable and completion gate |
| --- | --- |
| 1. Baseline and provider proof | Reproduce current installation blocker; establish SDK compatibility; prove Google account/channel discovery and grouping with authorized test accounts; record scope/identity/revocation behavior |
| 2. SDK and manifest | Add protocol 2.4 generic account/resource/management declarations and validators, typed clients and package/schema/template tests; keep old supported manifests working |
| 3. Host persistence and authorization | Enterprise connectors, account groups, authorization slots, resources, discovery runs, business grants and agent bindings; migration and isolation tests |
| 4. Execution boundary | Update broker/bootstrap/actions/media/results to the new authority context; tampering, revocation, concurrency and stale-approval tests must pass before enabling UI writes |
| 5. Generic install and Manage UI | One approval flow, redirect to plugin page, honest build status, Connect Account, native account/resource views, business grants, administrator-only prerequisites and recovery |
| 6. YouTube declarations | Move account setup, Google scopes/identity, channel pagination/projection/validation and permission labels into the plugin's new declarations; no host provider switches |
| 7. Agent binding | Update SDK/plugin package pins, authorized channel selection, exact plan correlation and recovery; regress existing reads/replies/uploads/edits/reporting |
| 8. Acceptance and rollout | Fake-provider/browser suites, real Google acceptance, cleanup verification, package-only builds/packs, current documentation and explicit replacement of old active references |

Phases 2–4 may be developed behind an explicit protocol/feature gate. Do not expose the new account UI backed by the old authorization model. Every phase has an independently reviewable result; no source deployment, real-account mutations or migration application is implied by approval of this plan.

## 11. Transition strategy

Do not silently combine historical business connections, merge credentials by email, or convert existing autonomous policies into enterprise authority. Old plugin/agent installations remain explicitly legacy until an administrator removes or replaces them. Existing unrelated service and agent workloads retain their supported paths.

New YouTube installations use the enterprise model and fresh explicit business/agent bindings. If an old account is connected again, treat it as new consent and new approval; never transfer credentials or standing policies automatically. In-flight old work must complete safely under its old valid authority or be visibly cancelled/blocked. Rollback preserves audit/data and blocks incompatible new work rather than running it through the old broker.

## 12. Acceptance criteria

Automated tests must prove:

- Install review shows accurate grants; one approval creates one installation; success lands on the plugin page with Connect Account. Disabled imports, missing profile configuration and build failures are correctly explained without hidden settings changes.
- Two accounts with overlapping-looking display labels remain separate, while repeated callbacks and repeated discovery do not duplicate account groups, channels, grants or jobs. Email changes cannot merge authority.
- A fake provider returns at least 100 resources across pages; restart resumes correctly, invalid cursors fail safely, and partial discovery does not revoke unseen resources. Channel identity cannot be inferred from public lookup results.
- Whole-account access includes subsequently verified/imported resources only after explicit future-resource acknowledgment. Selected-channel grants never expand. Existing agent bindings and standing policies do not silently expand with the business grant.
- Business and agent views cannot read other businesses' channel names, counts, drafts, reports, activity or credentials. Resource ownership and authorization are checked server-side, not through UI filtering.
- Grant revocation during ownership checks, queued approval or media transfer stops subsequent provider exchanges. Shared account access for another business remains valid where the provider authorization permits it.
- Approval tampering, cross-account substitution, stale authorization/grant revisions, raw HTTP bypasses, source attachment substitution and retry duplication fail.
- OAuth denial, partial consent, callback expiry/replay, refresh races, missing credentials, reconsent, provider revocation and disconnect recovery preserve correct state and never disclose secrets.
- A second fake provider supplies different labels, scopes, resource types and discovery behavior using only declarations. Its complete install/connect/grant flow requires no host application-specific code.
- Browser acceptance covers Install → Approve → plugin page → hosted provider consent → imported resources → selected/whole-account grant → agent selection → an exactly approved action → selective revoke → full disconnect. Include keyboard and narrow-screen use.
- Package tests, SDK template verification, build/test/pack and package-only consumer verification pass with synchronized versions.

Real-provider acceptance must additionally demonstrate actual Google consent, verified account grouping, paginated discovery where available, a channel requiring separate authorization, accurate partial-access messaging and revocation effects. Use authorized test channels and explicit approval for any external mutation. Existing unfinished retention/cleanup work remains a production release gate.

## 13. Definition of done

The reviewed simple journey works end to end in C-Sweet; the plugin supplies its setup, discovery, permission and management declarations; business/agent/resource authority is enforced through the entire broker; a second provider works without host-specific changes; required package and browser checks pass; and real-provider limitations are documented accurately. A clickable prototype or passing fake-provider suite alone does not satisfy this definition.
