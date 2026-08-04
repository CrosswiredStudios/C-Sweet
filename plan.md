# Secure Source Control, Managed GitHub, and Git Workspace v2

## Implementation status (2026-08-03)

- [x] Replace shared agent/work contracts and SDK capability names with v2-only contracts.
- [x] Migrate Software Developer, Software QA, Software Architect, and Software Product Manager
  package versions/manifests to the new contracts.
- [x] Remove caller-selected repository URLs, refs, branches, and commit SHAs from agent workspace
  requests.
- [x] Replace per-agent Git credentials and repository grants with authoritative business/team
  repository policy checks.
- [x] Persist exact-SHA publications, QA validations, team-lead authorizations, and merge jobs.
- [x] Remove the direct GitHub merge executor and fail closed through a trusted-host boundary.
- [x] Add the destructive `ResetSourceControlV2` database migration; no runtime compatibility
  layer remains for the old Git tables.
- [x] Add the first Source Control business page and update development assignment onboarding copy.
- [x] Add separate GitHost and ProvisionerHost projects with independently supplied GitHub App
  keys, HMAC-authenticated internal requests, timestamp/nonce replay protection, provider
  installation verification, bounded repository discovery, exact-SHA merge, and a private-only
  template provisioning surface. No generic provider proxy or token-bearing response exists.
- [x] Add Core HTTP clients and Aspire wiring that activate only when trusted-service configuration
  is complete. Missing configuration continues to fail closed with no local/provider fallback.
- [x] Add resumable GitHub onboarding with one-time state rotation, separate Source Access and
  Provisioner installation steps, organization-only Managed GitHub enforcement, database-backed
  cross-business account isolation, provider-verified existing-project selection, approved
  template selection, naming/quota/default-team policy, and plain-language Source Control UI.
- [x] Add durable managed-repository execution, private-template creation, fixed branch protection,
  partial-creation quarantine, automatic Core registration/team assignment, manager approval
  fulfillment, approval inbox cards, important deduplicated notifications, and realtime snackbars.
- [x] Pin Core to the breaking `CSweet.Agent.SDK` and `CSweet.WorkManagement.Contracts` 3.0.0
  packages so a non-local build cannot silently restore v1-era contracts.
- [x] Add the sanitized workspace prepare bridge. AgentHost now sends only opaque assignment IDs
  over a separately derived AgentHost-to-Core HMAC trust domain; it never receives the Core-to-
  GitHost key. Core re-resolves the current organization/installation/work-item/repository state,
  GitHost fetches into a disposable bare repository without checking out or executing code, both
  sides reject `.git`, links, unsafe paths, and oversized archives, and Core imports the verified
  snapshot through a short-lived networkless helper mounted to exactly one per-installation Docker
  volume. Agents can build and test the fetched sanitized source in their existing isolated runtime.
- [ ] Complete sanitized workspace refresh, inspection, publication, and cleanup. These operations
  remain fail-closed until Core exports a validated complete snapshot, GitHost reconstructs it in a
  clean credentialed checkout, and commit/push/PR state is returned without exposing credentials,
  provider installation IDs, `.git`, Docker storage, or trusted host paths to AgentHost or agents.
- [ ] Complete connection health/recovery, suspend/disconnect impact previews, generic Git credential
  rotation, repository/team delivery maintenance pages, and end-to-end browser tests.
- [ ] Before public or multi-tenant GitHub onboarding, add GitHub user authorization during App
  installation and verify that the authenticated installer can access the returned installation.
  One-time C-Sweet state plus App-level installation lookup prevents fabricated installations but
  does not by itself prove the current user's GitHub authority, as GitHub's setup-URL guidance warns.
- [ ] Add durable GitHost idempotency storage and recovery of ambiguous provider timeouts before
  enabling automatic retries for repository creation or publication.

Verification for the current slice:

- `dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --no-restore`: 433 passed, 7 existing skips.
- `dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --no-build --no-restore`: 65 passed.
- `dotnet build CSweet.slnx --no-restore`: succeeded with 0 warnings and 0 errors.

Summary
Replace Git workspace v1 wholesale with:
Business-scoped source-control connections supporting multiple accounts.
A nontechnical Managed GitHub mode that can create secure private repositories.
Assignment-scoped Git workspace v2 MCP capabilities.
Trusted Git and repository-provisioning services outside agent containers.
Guided setup, maintenance, recovery, approvals, and contextual onboarding.
Governed merging authorized by the team lead.
No v1 compatibility layer will remain. Existing Git configuration will be reset and affected first-party agents will migrate together.
Connection Modes
Source-control onboarding offers three choices:
Managed GitHub — RecommendedCSweet creates and configures private repositories in a connected GitHub organization.
Initially organization accounts only; personal-account creation is excluded because it requires user access tokens.

Connect existing projectsThe user selects existing repositories available to the normal CSweet GitHub App.

Advanced GitGeneric HTTPS or SSH repository configuration.
Supports fetch, ticket-branch publication, and external merge only.

Use two separate GitHub Apps:
CSweet Source Access AppContents, pull requests, metadata, and required-check access.
Used for normal development, QA, PRs, and governed merge.

CSweet Repository Provisioner AppRepository Administration: write.
Used only to create repositories and apply approved baseline settings.
Never handles source archives, workspaces, builds, or agent traffic.

Trusted Service Boundaries
Add CSweet.GitHost for credentialed fetch, commit, push, PR, and merge operations.
Add CSweet.SourceControlProvisionerHost for repository creation and baseline configuration.
Both consume durable Core-owned jobs and are inaccessible from agent networks.
ProvisionerHost:does not clone or execute repository content;
uses an independently protected App key;
exposes no deletion, public-visibility, collaborator, ownership-transfer, secret, or arbitrary-settings operation.

GitHost:uses disposable clean checkouts;
disables hooks, filters, submodule recursion, and unsafe protocols;
validates archive paths, symlinks, file counts, sizes, and hashes;
never executes builds or tests.

Agent containers receive credential-free working trees without .git, remotes, or tokens.
Developer and QA agents continue to build and test in isolated, non-root, secretless sandboxes.
Core Data Model
SourceControlConnectionBusiness, provider, mode, external account identity, installation identities, status, and revision.
One business may have multiple accounts; cross-business sharing is prohibited.

SourceControlRepositoryConnection, provider repository ID, normalized path, default branch, provider capabilities, origin, health, and lifecycle state.

RepositoryProvisioningPolicyBusiness connection, enabled state, standing-versus-manager approval, private-only enforcement, naming convention, template, quota, default team, branch policy, and revision.

RepositoryProvisioningRequestApproved product/workstream, requester, normalized repository specification, policy snapshot, status, resulting repository, and idempotency key.

TeamRepositoryPolicyTeam, repository, workspace/publication access, merge mode, revision, and status.

Durable Git workspace, operation-job, artifact, merge-authorization, merge-approval, connection-health, and onboarding-session records.
Expiring assignment grants bind:business;
repository;
work item and assignment revision;
agent installation revision;
base SHA;
permitted operation;
policy/grant revision.

OrganizationTeam.LeadOrganizationUserId is the canonical merge authority. Titles such as Software Architect do not independently grant authority.
Managed Repository Provisioning
Expose source-control.repository.provision.v2 only to approved Product Manager or Software Architect installations.
Developer and QA agents never receive repository-provisioning authority.
The request contains only:approved product/workstream ID;
desired project display name;
bounded description;
approved template ID;
idempotency key.

Core derives the business, GitHub organization, connection, private visibility, normalized name, team, branch policy, and quota.
Provisioning requires:an active Managed GitHub connection;
an approved product/workstream;
a current provisioning policy;
an eligible requester;
available quota;
a unique normalized name.

Policy modes:Standing authorization: eligible Product Manager or Architect requests are created automatically.
Manager approval: create an approval and snackbar before provisioning.

Every created repository is:private;
initialized from an approved template;
assigned a deterministic name and description;
configured with the approved default branch and branch protection;
registered automatically in CSweet;
assigned to the configured team;
handed off to the lower-privilege Source Access App.

Agents cannot request:public visibility;
arbitrary owners or organizations;
repository deletion;
collaborator changes;
ownership transfer;
secrets or deployment keys;
arbitrary workflows or webhooks;
disabling branch protection.

Provisioning failures are idempotent and recoverable. A partially created repository is quarantined for administrator review rather than deleted automatically.
Git Workspace v2
Remove every git.workspace.*.v1 capability and expose only:
git.workspace.prepare.v2Derive repository, branch, ref, and base SHA from the assignment.
Materialize a credential-free snapshot.

git.workspace.refresh.v2Reapply current changes against the authorized base and return structured conflicts.

git.workspace.inspect.v2Return bounded file and diff metadata.

git.workspace.publish.v2Capture the change artifact.
GitHost reconstructs, signs, commits, pushes, and creates or updates a GitHub PR.

git.workspace.cleanup.v2Remove or retain the bounded workspace.

git.merge.review.v2Expose the exact candidate SHA, bounded diff, QA evidence, and required checks to the assigned team lead.

git.merge.authorize.v2Let that lead approve or reject the exact SHA without receiving credentials.

V2 contracts remove caller-selected connection, repository, remote, base branch, branch name, and ref fields.
Replace RepositoryConnectionId with RepositoryId throughout work-management and agent contracts. Eliminate hard-coded assignment revision 0; every request carries the authoritative assignment revision.
Publish returns:
provider;
delivery kind (PullRequest or BranchOnly);
deterministic branch;
commit SHA;
nullable PR URL;
publication status.
No agent can access arbitrary refs, remotes, tags, force push, protected branches, credentials, or installation IDs.
Delivery and Merge Workflow
GitHub
Developer prepares, implements, builds/tests, inspects, and publishes.
GitHost creates the ticket branch and PR.
QA receives a fresh exact-SHA snapshot, validates it, and confirms no tracked source modifications.
The team lead reviews and approves or rejects that exact SHA.
Team merge policy determines the next action:LeadAuthorizedAutoMerge: queue the trusted merge.
LeadAndAdminApproval: create a manager/owner approval.

GitHost rechecks the PR head, QA evidence, provider checks, branch protection, grants, policies, and approvals before merging.
Any head change invalidates QA and all approvals.
Generic Git
Fetch and deterministic ticket-branch publication only.
After QA, delivery ends as BranchPublishedExternalMerge.
No CSweet PR or merge action is exposed.
Team repository policy replaces board-level merge-mode authority and is snapshotted into each execution.
Source-Control UI
Add Source control to Settings with:
Accounts
Connection cards showing account, provider, mode, repository count, last verification, and health.
Actions for reconnect, test, synchronize, rotate credentials, suspend, and disconnect.
Separate status for the Source Access App and Repository Provisioner App.
Repositories
Searchable list with account, visibility, team, readiness, default branch, source, and delivery capability.
Distinguish:CSweet-managed;
connected existing;
generic external.

Managed repositories show template, provisioning request, policy revision, and creation audit.
Team delivery
Assign repositories to teams.
Display and validate the current team lead.
Configure:branch publication;
lead-authorized automatic merge;
lead plus manager approval;
generic external merge.

Show readiness blockers before saving.
Connection detail
Add /settings/source-control/connections/{connectionId} with:
overview;
permissions;
repositories;
provisioning policy and quota;
team usage;
connection health;
generic credential rotation;
audit history;
suspend/reconnect/disconnect.
Guided Nontechnical Onboarding
Keep source control out of general business creation. Launch contextual Connect your code onboarding when software work first needs it.
The wizard contains:
How should CSweet manage your code?Managed GitHub
Existing GitHub projects
Advanced Git

Choose your GitHub organizationPlain-language account explanation.

Authorize CSweetExplain Source Access and Provisioner permissions separately.
Managed mode guides the user through both App installations.

Choose or create projectsExisting mode selects repositories.
Managed mode configures naming, template, quota, and default team.

Choose how changes are approvedTeam lead approves and CSweet merges.
Team lead and manager both approve.
Branch only.

Verify readinessConnection, permissions, repository creation, branch publication, PR, checks, lead, and team policy.

ReadyPlain-language summary and link to create software work.

Use language such as:
“Code project” before “repository.”
“Proposed change” before “pull request.”
“Main version” before “default branch.”
The wizard is resumable and dismissible. Only actions requiring source control are blocked.
Add contextual readiness banners and Set up source control actions to:
Command Center;
software work empty states;
team setup;
development assignment dialogs;
product/workstream setup when no repository is available.
Maintenance and Recovery
Connection states:Connected
Needs attention
Suspended
Disconnected

Repository states:Ready
Missing permission
Removed
Archived
Connection unavailable
Provisioning incomplete

Detect GitHub installation suspension, repository removal, permission changes, organization policy rejection, and Provisioner App removal.
Detect generic authentication, DNS, host, and SSH fingerprint changes. Never trust a changed fingerprint automatically.
Provisioner disconnection prevents new repository creation but does not interrupt existing repository delivery through the Source Access App.
Disconnect impact preview lists affected teams, active work, pending PRs, provisioning requests, and approvals.
Confirmed disconnect revokes grants, invalidates approvals, blocks affected work with remediation guidance, removes generic credentials, and preserves audit history.
Only business owners and managers manage connections and policies.
Approvals and Notifications
Add approval kinds:
RepositoryProvisioning
Merge
Provisioning approval cards show:
product/workstream;
requested repository name;
GitHub organization;
private visibility;
template;
default team;
requester;
policy and quota.
Merge approval cards show:
repository;
work item;
PR;
exact SHA;
QA result;
team-lead decision;
policy.
Pending approvals create deduplicated important notifications and realtime snackbars:
“New code project approval needed”
“Code merge approval needed”
Each snackbar has a Review action linking to Approvals. Decisions are idempotent; rejection requires feedback.
Agent and Package Migration
Release breaking package versions:
CSweet.WorkManagement.Contracts 3.0.0
CSweet.Agent.SDK 3.0.0
Migrate first-party agents:
Software Developer 0.3.0Git workspace v2;
actual assignment revision;
GitHub PR and generic branch-only results.

Software QA 0.2.0exact-SHA v2 prepare/inspect/cleanup;
no tracked-source modification.

Software Architect 0.4.0merge-review v2;
merge authorization when team lead;
managed repository provisioning when granted.

Software Product Manager 2.0.0RepositoryId;
managed repository provisioning when granted;
no operational Git or merge credentials.

Update manifests, package pins, catalog entries, documentation, and tests. Remove all v1 Git strings, handlers, DTOs, schemas, and tests.
Installed revisions containing v1 Git grants are disabled and require a one-time update and grant review.
Reset and Verification
Delete old repository connections, installation grants, workspaces, merge records, and installation-scoped Git secrets.
Revoke related scoped grants.
Cancel active executions referencing old repository IDs with reconnect/reassign instructions.
Require businesses to reconnect accounts and configure team policies.
Tests cover:
Multiple businesses, accounts, and repositories with cross-business denial.
Managed organization setup and rejection of personal-account managed provisioning.
Separation of Source Access and Provisioner credentials.
Private-only creation, quotas, naming collisions, templates, idempotency, and approval modes.
Denial of provisioning to Developer/QA and unapproved Product Manager/Architect installations.
Absence of deletion, public visibility, collaborator, transfer, and arbitrary-settings operations.
GitHub organization policies that prohibit App repository creation.
Partial provisioning recovery without automatic deletion.
Git workspace isolation, malicious repository content, secret leakage, stale assignments, replay, and revoked policy.
Developer publication, exact-SHA QA, team-lead authorization, admin approval, stale-head invalidation, and governed merge.
Generic branch-only delivery.
Complete removal of v1 capabilities.
Full onboarding, maintenance, health, approval, snackbar, accessibility, and responsive UI flows.
Assumptions
Managed GitHub initially supports GitHub organization accounts only.
Managed repositories are always private.
Repository creation may be standing-authorized or manager-approved per business policy.
Repository deletion and visibility changes remain human-only GitHub operations outside CSweet.
Auto-merge means automatic platform merge after a per-PR team-lead decision.
Generic Git remains branch-only.
Agents receive narrow business authority but never provider credentials or unrestricted GitHub administration.</proposed_plan>
