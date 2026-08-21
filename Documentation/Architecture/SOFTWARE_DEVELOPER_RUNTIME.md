# Secure source control and software delivery plan

## Status and product decisions

This document is the implementation plan for source control in C-Sweet. It replaces the original
Software Developer runtime design. C-Sweet is not released, so this is a wholesale replacement:
the Git workspace v1 contract, its data, and its credential flow will be removed rather than
supported alongside v2.

The approved product decisions are:

- A business can connect multiple source-control accounts, including accounts owned by different
  GitHub organizations or different generic Git hosts. Connections and repositories cannot be
  shared across businesses.
- GitHub and generic Git are supported. GitHub receives pull-request and governed-merge support;
  generic Git publishes a ticket branch and ends in `BranchPublishedExternalMerge`.
- Users can connect existing repositories or choose **Managed GitHub**, which lets C-Sweet create
  private repositories for them within an approved GitHub organization.
- Managed GitHub is organization-only initially. Creating a repository in a personal GitHub
  account requires a user access token and is outside the first release.
- Agents may edit files and build and execute untrusted project code in an isolated workspace.
  Agents never receive source-control credentials, provider tokens, or permission to perform
  authenticated Git operations.
- A trusted Git service fetches, commits, pushes, opens pull requests, and merges. A separately
  isolated provisioning service creates repositories.
- The canonical team lead, represented by `OrganizationTeam.LeadOrganizationUserId`, must approve
  the exact commit before it can merge. Each team chooses lead-authorized auto-merge or lead plus
  administrator approval.
- Repository provisioning and administrator merge approval appear in the Approvals page and as
  real-time snackbar notifications.
- Existing Git connections, grants, workspaces, and associated credentials are development data;
  the migration deletes them and requires users to reconnect.

## Security boundary

Plugins and agent workloads remain untrusted. They have no direct access to GitHub, generic Git
servers, source-control secrets, the Docker daemon, or the host filesystem. A trusted service
delivers a ticket-scoped, credential-free source artifact through the authenticated broker; the
agent expands it only inside its disposable hardware-virtualized guest. It may read and change
that guest-local working tree and run the approved build and test commands inside the same guest.

Authenticated source-control operations cross a narrow platform capability boundary:

1. Core resolves the business, team, work item, assignment revision, repository, and policy from
   durable state. These values are not accepted from the agent.
2. Core issues a short-lived, single-operation job to the trusted Git host.
3. The Git host obtains a repository-scoped credential just in time, validates the remote and
   expected ref, performs the operation, and destroys the credential.
4. Core records the normalized result and an append-only security audit event. Provider responses,
   command output, and errors are scrubbed before they reach an agent.

The agent cannot choose a connection, repository, ref, branch name, pull request, merge target, or
credential. Capability possession alone is insufficient: every call is re-authorized against the
current organization, installation, team membership, assignment revision, repository policy, and
work-item state.

### Trusted services

`CSweet.GitHost` is a first-party service, not an installable plugin or agent. It owns clone/fetch,
workspace refresh, diff inspection, commit creation, push, pull-request creation, head validation,
and merge. Its network policy allows only the resolved repository host and required provider API.
It never makes credentials available inside the agent workspace or command environment.

`CSweet.SourceControlProvisionerHost` is a separate first-party service. It owns GitHub repository
creation and baseline configuration but has no permission to read or write repository contents.
Separating provisioning from source access limits the effect of either service being compromised.

Untrusted builds run in the existing software-development sandbox with a read-only root,
non-root UID, dropped capabilities, `no-new-privileges`, CPU/memory/PID/time limits, and controlled
egress. Workspaces are disposable and scoped to one business, repository, work item, assignment,
and agent installation. The build sandbox cannot reach either trusted service except through the
authenticated Core capability broker.

## Connections and repository inventory

The v2 data model separates an account connection from a repository:

- `SourceControlConnection` belongs to exactly one business and describes one provider account or
  Git host. It stores provider identifiers and health state, never a plaintext secret.
- `SourceControlCredential` is a Core-owned, encrypted, write-only generic Git credential with
  rotation and revocation history. It is never stored in plugin secret storage or returned by an
  API. GitHub installation tokens are short-lived and are not persisted here.
- `SourceControlRepository` belongs to one connection and one business. It records the immutable
  provider repository identifier, canonical owner/path, private/managed status, default branch,
  and health state.
- `TeamRepositoryPolicy` binds a team to a repository, selects its primary delivery repository,
  and chooses `LeadAuthorizedAutoMerge` or `LeadAndAdministratorApproval`.
- `RepositoryProvisioningPolicy` limits which GitHub organization, templates, naming pattern,
  quota, teams, and defaults Managed GitHub may use.
- `RepositoryProvisioningRequest` is an idempotent, auditable request and, when required by policy,
  references a manager approval.
- Durable workspace, validation, publication, review, authorization, and merge records bind every
  decision to an exact repository and commit SHA.

Connection modes are:

1. `ManagedGitHub` — recommended for nontechnical users; install both C-Sweet GitHub Apps and let
   C-Sweet create private repositories under policy.
2. `ExistingGitHub` — install the source-access app on selected repositories.
3. `GenericGitHttps` — advanced mode using a write-only HTTPS credential.
4. `GenericGitSsh` — advanced mode using a write-only SSH key and pinned host fingerprints.

All queries and uniqueness constraints include the business boundary where appropriate. A
repository assignment is rejected unless the connection, repository, team, work item, and agent
installation resolve to the same active business.

## GitHub Apps and Managed GitHub

Two GitHub Apps are used:

- **C-Sweet Source Access** receives only the repository permissions needed for contents,
  pull requests, and checks/status visibility. Installation access tokens are minted just in time,
  narrowed to the target repository and required permissions, and never stored.
- **C-Sweet Repository Provisioner** receives organization repository administration write access
  so it can create repositories. It is installed only for organizations using Managed GitHub and
  is not used for routine source operations.

Managed repository creation is exposed as `source-control.repository.provision.v2` only to the
Product Manager and Software Architect roles when their current grants and the business policy
permit it. A request must name an approved product/workstream and template and must pass naming,
quota, organization, team, and idempotency checks. Depending on policy, it uses standing
authorization or creates a manager approval.

The provisioner always creates a private repository and applies the approved baseline: default
branch, template, source-app access, team repository policy, and branch/ruleset configuration. The
capability cannot delete or transfer a repository, make it public, add arbitrary collaborators,
manage actions secrets, bypass quotas, or change arbitrary repository settings. Existing GitHub
organization policy may still prevent creation; that becomes a clear, recoverable onboarding
status rather than broader permissions.

## Agent Git workspace v2 contract

The SDK and all first-party software agents move together to these capability names:

- `git.workspace.prepare.v2`
- `git.workspace.refresh.v2`
- `git.workspace.inspect.v2`
- `git.workspace.publish.v2`
- `git.workspace.cleanup.v2`
- `git.merge.review.v2`
- `git.merge.authorize.v2`
- `source-control.repository.provision.v2`

There is no v1 compatibility shim. Request contracts no longer contain
`RepositoryConnectionId`, a clone URL, a base ref, a branch name, or a caller-supplied repository.
Core resolves the `RepositoryId` from the actual team/work-item assignment. Every request carries
the real assignment revision; a magic revision such as `0` is invalid.

The v2 role surface is:

- Software Developer: prepare, refresh, inspect, publish, and cleanup its assigned workspace.
- Software QA: prepare/refresh a fresh read-only validation workspace for the published exact SHA,
  inspect it, record validation, and cleanup.
- Software Architect: review the exact publication, authorize its exact SHA as team lead when it
  is the canonical lead, and request managed repository provisioning when granted.
- Software Product Manager: select repository intent for a product/workstream and request managed
  repository provisioning when granted. It cannot push or merge code.

SDK and agent packages are released together because their current public contracts are replaced:

- `CSweet.WorkManagement.Contracts` 3.8.0
- `CSweet.Agent.SDK` 3.11.1
- Software Developer 0.5.0
- Software QA 0.4.0
- Software Architect 0.7.0
- Software Product Manager 2.5.0

## Delivery state machine

1. Assignment creates a durable workspace job for the resolved repository and assignment revision.
2. The Git host materializes a clean credential-free tree. The developer edits, builds, executes,
   and tests code in the sandbox.
3. Publish asks the Git host to inspect the tree, create a deterministic ticket-branch commit,
   push it, and create or update a GitHub pull request. The author shown in audit history is the
   acting agent installation; authentication remains the C-Sweet GitHub App.
4. QA validates a fresh workspace at that exact commit SHA. A later head change invalidates the QA
   result and every approval.
5. The canonical team lead reviews and authorizes that exact SHA.
6. With `LeadAuthorizedAutoMerge`, Core asks the Git host to merge once required checks and branch
   policy pass. With `LeadAndAdministratorApproval`, Core creates an administrator approval,
   notifies eligible administrators, and merges only after approval of the same SHA.
7. Generic Git stops after the deterministic branch is published and reports
   `BranchPublishedExternalMerge`; C-Sweet does not claim that it merged.
8. Completion records immutable artifacts and removes the disposable workspace. Failed workspaces
   are retained for a short configured diagnostic window without credentials.

Merge authorization is never a standing agent permission. It is a signed, expiring decision over
the business, repository, pull request, target branch, exact head SHA, policy revision, and
authorizing user. Reassignment, policy change, repository disconnect, new commits, failed checks,
or expired authorization makes it unusable.

## User experience and onboarding

Add **Source control** to business settings with three pages or tabs:

- **Accounts** lists GitHub and generic Git connections, account/organization identity, mode,
  installed-app state, health, last verification, and reconnect/disconnect actions.
- **Repositories** lists connected and managed repositories, owning account, team/product use,
  default branch, private/managed state, health, and provisioning progress.
- **Team delivery** assigns the primary repository and merge approval mode for each software team
  and explains who the current team lead and administrators are.

Each connection has a detail page for app installation state, repository selection, host-key or
credential rotation for advanced Git, health checks, audit history, and a disconnect-impact
preview. Secrets are write-only and are never redisplayed.

Use a contextual **Connect your code** wizard from Command Center readiness, Work Boards, and
software-team setup. Do not force it into creation of every business. The plain-language flow is:

1. Explain that C-Sweet needs a safe place to store and deliver the team's code.
2. Offer **Let C-Sweet set it up** (Managed GitHub), **Use existing GitHub repositories**, and
   **Advanced Git server**.
3. Select or connect the GitHub organization/account; install only the app or apps required by the
   chosen mode.
4. Create private repositories from an approved template or select existing repositories.
5. Assign each repository to a team/product and choose the approval mode, with a plain-language
   recommended default.
6. Verify permissions and policy with actionable remediation text.
7. Show a summary of what agents can do, what they cannot do, who approves merges, and how to
   return to settings.

The wizard and settings must be keyboard accessible, responsive, resumable after OAuth/App
installation redirects, safe under refresh/back navigation, and written for users unfamiliar with
Git terminology. Destructive or disruptive actions show their exact effect before confirmation.

Repository provisioning and administrator merge approvals add dashboard kinds
`RepositoryProvisioning` and `Merge`. New pending items produce real-time snackbar notifications
with a link to the Approvals page; decisions update all open clients and the affected work item.

## Destructive development migration

Because the product is unreleased, the database migration intentionally removes the old
`GitRepositoryConnections`, `GitRepositoryConnectionGrants`, `GitTicketWorkspaces`, and their
credential records after canceling active affected executions. It then creates the v2
source-control tables. There is no attempt to translate old credentials or grants. After upgrade,
the UI reports that source control must be reconnected.

The code migration removes all `git.workspace.*.v1` names and DTOs from Core, MCP schemas,
`CSweet.Agent.SDK`, Work Management contracts, Developer, QA, Architect, and Product Manager. A
repository-wide test fails if a production project still contains a v1 Git capability name or the
old caller-controlled `RepositoryConnectionId` workspace field.

## Implementation phases

### Phase 1 — domain and persistence foundation

- Add the v2 connection, repository, provisioning policy/request, team policy, onboarding session,
  encrypted credential, workspace job, publication, validation, exact-SHA authorization, and
  merge job entities.
- Add tenant-safe keys, concurrency revisions, lifecycle states, and indexes.
- Add the destructive migration and explicit startup/reconnect status.
- Add policy and tenant-isolation unit tests.

### Phase 2 — trusted hosts and provider adapters

- Create narrow Core job contracts for GitHost and ProvisionerHost.
- Implement GitHub App token minting with repository and permission narrowing.
- Implement generic HTTPS/SSH adapters, pinned host verification, command/output scrubbing,
  deterministic branches, idempotency, retry limits, and audit events.
- Implement credential-free workspace handoff and isolated build/test execution.

### Phase 3 — v2 broker, SDK, and agents

- Replace the v1 MCP catalog, handler, DTOs, grants, and completion requirements.
- Release the breaking SDK/contracts packages and update all four agents together.
- Add Developer publish, QA fresh-SHA validation, Architect review/authorization, and PM/Architect
  provisioning behavior.

### Phase 4 — delivery governance and approvals

- Implement team-lead resolution, exact-SHA authorization, invalidation, branch-policy/check
  evaluation, both approval modes, merge jobs, approval dashboard kinds, notifications, and audit.
- Ensure generic Git produces only the external-merge handoff state.

### Phase 5 — settings and guided onboarding

- Implement Accounts, Repositories, Team delivery, connection detail, health, audit, and impact UI.
- Implement the resumable Connect your code wizard and contextual readiness/Work Board entry points.
- Add plain-language help, empty/error/loading states, accessibility checks, and responsive tests.

### Phase 6 — hardening and release gate

- Threat-model confused-deputy, cross-business, malicious repository, credential-exfiltration,
  stale-SHA, replay, SSRF, symlink, hook, submodule, and build-escape attacks.
- Test private-only provisioning, quotas, templates, idempotency, app separation, revocation,
  rotation, disconnect, and provider outage recovery.
- Run integration tests against disposable GitHub organizations/repositories and generic Git hosts,
  plus full SDK/agent package and Core regression suites.

## Acceptance criteria

- No agent or plugin can obtain a source-control secret or make an authenticated provider call.
- An agent can build and execute tests only inside its ticket-scoped sandbox and can publish only
  through a re-authorized trusted-host operation.
- Every source-control object and operation is business-scoped and cross-business identifiers fail
  closed without leaking existence.
- Managed GitHub creates only policy-compliant private organization repositories and exposes none
  of the prohibited administration operations.
- A changed head SHA invalidates QA and approval state; merge requires the current team lead and,
  when configured, an administrator to approve the same exact SHA.
- Nontechnical users can complete Managed GitHub setup without knowing clone URLs, credentials,
  branches, or pull-request mechanics.
- Existing-account, multi-account, multi-repository, reconnect, health, and disconnect flows are
  maintainable from the Source control settings UI.
- No production code contains the Git workspace v1 contract after the coordinated migration.
