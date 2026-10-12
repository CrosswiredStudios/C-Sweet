# Project compute scope

`ComputeDefaultsService.EnsureRequestedAsync` prepares one organization-wide `ComputeLocalSetup`.
Agent startup never creates a `Workstream` or `ComputeAgentAccess`, regardless of the installed
manifest's `rolePolicy.requiresProject` setting. An explicit retry can reset a failed setup;
ordinary agent restarts cannot.

`ComputeDefaultsService.ReadProjectAsync` resolves testing permissions against an existing
approved or active project. It verifies the current employee, installation capabilities and
project manager, supervision or team assignment, then creates bounded project-scoped compute
grants. Existing grants, including revocations and owner edits, are preserved. Network authority
is not created automatically. Agents declaring `requiresProject` also retain the explicit
participant, delivery binding, team and board grant checks in `ProjectWorkPolicy.RequireAsync`.
Projects using established team assignments can request compute without a newer delivery binding.

`ComputeDefaultsService.ReadAsync` supports callers that omit the project only when they have
one current authorized project. No assignment returns `project_required`; multiple assignments
return `project_selection_required`. Neither response supplies a scope or runnable template.
Legacy automatic workspace records are never selected as a fallback. Setup readiness wakes
current project managers, supervisors, participants and team members through the durable
`com.csweet.compute.available.v1` outbox; consumers must reread current authority and defaults.

`SpecialistAgent.ReconcileTeamComputeAsync` in the Technical Director repository already uses
`GetProjectDefaultsAsync(setup.WorkstreamId)` and binds environment requests to that project.
This platform correction does not require an agent or SDK package change.

## Overview attention queue

`CommandCenter.ComputeAccessRequests` includes local browser-link permissions from the authorized
`ComputeDashboardEndpoints.ReadAsync` projection in **Needs your attention**. Only a current human
business owner receives actionable cards. Already allowed, expired, destroying, destroyed and
release-requested instances are excluded. `PendingAgentQuestions` includes these cards in the shared
pending count and five-item expansion control; **Review access** links to the instance's browser-access
panel on `Compute`. Compute loading and errors remain independent of the other attention sources.
Both pages use the dashboard's existing bound of 100 recent instances; a truncated read displays a
notice instead of claiming there is nothing pending.

`CSweetDbContext.CaptureComputeEvents` persists `AppRealtimeEvents.ComputeChanged` in the application
realtime outbox with environment lifecycle/lease changes and inbound/publish-port grant changes.
The payload is a wake hint, routed only to current active, unarchived human organization members.
`CommandCenter` rereads authorized state on these events and connection/reconnection, including a
follow-up refresh when a wake arrives during loading. This dismisses allowed access and restores revoked
access without creating an unrelated approval record. `CompanyDashboardRenderingTests`,
`CompanyDashboardRealtimeTests`, `ComputeExplicitNetworkGrantTests` and `ApplicationRealtimeEventTests`
cover the queue, owner visibility, independent failures, realtime recovery and durable notifications.

## Existing generated projects

`scripts/maintenance/Rebind-GeneratedComputeProject.sql` accepts explicit psql variables
`organization_id`, `installation_id`, `generated_project_id` and `existing_project_id`.
It checks an untouched automatic `Application testing` project, an authorized active destination,
and all direct project references before removing the generated record. Environments, boards,
delivery data and substantive activity block removal. Only empty automatic health records are
removed, with their snapshots retained in the audit outbox. Grants on the old scope are revoked.

The retained legacy access record is rebound to the existing project and marked initialized so
an older running host cannot recreate the generated project. The audit and compute wake commit
atomically with the repair. Repeating a completed repair makes no changes. Restart the platform
with the updated build to use the corrected defaults and authorization flow.

`ComputeProjectScopeTests`, `ComputeAutomaticSetupTests`, `ComputeCapabilityTests` and
`ComputeBrokerTests` cover scope reuse, admission, revocation, current membership, manifest policy,
selection ambiguity, explicit retries and existing environment recovery.

Verification on October 11, 2026: 76 focused compute and project tests passed in
`ProjectComputeVerified`. A transaction against the running PostgreSQL database verified that
Victor's explicit and inferred defaults select Prismatic Break, report `Ready`, and create six
bounded grants on that scope; verification writes were rolled back. The live cleanup committed,
its audit was delivered, and a repeated repair made no changes.
