# Phase 2 · Connect accounts to enterprise membership

**User story.** As an invited member, I can use my shared or private identity and see the businesses granted to me.

**Demonstration.** Demonstrate linked first-run registration, private first run with no outbound traffic, an existing Market customer signing in, and access isolation with two businesses and two issuers reusing an email.

## Detailed guides

The five issues below are epics. Take work from the issue lists in these guides, which name files, types, tests and done checks:

| Epic below | Issue list |
| --- | --- |
| 1 · Headquarters member | [Multi-user Headquarters](multi-user-headquarters.md) MU-01 to MU-07, MU-10 to MU-12 |
| 2 · Invitations and People & access | Same guide, MU-08 and MU-09 |
| 3 · C-Sweet account and Market | [Identity and installation linking](identity-and-installation-linking.md) IL-01 to IL-11, IL-13 |
| 4 · Local accounts and company OIDC | Local accounts: MU-04 and MU-08. Company OIDC: not yet broken down |
| 5 · Directory | IL-06 and IL-12 |

Order: MU-01 to MU-03 and IL-01 to IL-02 first, in parallel. No member other than the administrator may be created until MU-12.

## Before starting

Read `AuthenticationEndpoints`, `AuthenticationService.RegisterAsync`, `ApplicationUser`, `OrganizationUserEndpoints` and `ScopedActionAuthorizationService`. Distinguish the authenticated person from the business employee record. Inspect Market Identity registration, signed-in account components and `IdentityNoOpEmailSender`. Prepare two test issuers, test delivery for shared email, and a private network with public egress blocked.

## Implementation issues, in order

### 1. Establish the Headquarters member

Associate the local application user with enterprise privileges and business grants. Preserve existing initial administrator and recovery behavior on upgrade. Audit list, detail, command, receipt and event authorization in affected web services before enabling ordinary members. Share actor-aware service checks with mobile.

### 2. Implement invitations and People & access

Add invite/review/resend/cancel/accept and grant edit/removal flows. Keep invitation secrets single use and server-side hashes scoped to issuer and intended recipient. Commit grants, audit and directory outbox together. Recheck authority at acceptance. Test duplicate redemption and stale administrator edits.

### 3. Make Market the C-Sweet account issuer and link at first run

Make Market the issuer: a C-Sweet account is a Market account, so user ID, roles and purchases are untouched and no linking is needed. Send real verification and recovery email. Add the hosting choice to first run at Headquarters; **Connect to C-Sweet** creates the administrator's account by device authorization during registration and registers the installation. The phone uses browser OIDC with PKCE. Recover a pending verification or device code after the user leaves.

### 4. Implement local accounts and company OIDC

In private mode deliver an administrator-issued activation invitation by an internal channel, set credentials within a pinned Headquarters session and provide local administrator recovery. For company OIDC configure exact trusted issuer, audience and callback, persist issuer/subject and verify membership at HQ. Test with external services unreachable.

### 5. Publish the optional directory projection

Implement authenticated self lookup and installation-bound publication/reconciliation. Include revisioned removals so an older upsert cannot re-add a member. Private-only installations do not publish. Phase 3 consumes restricted enrollment tickets and Phase 4 binds the directory to the chooser.

## Acceptance example

Invite Maya to Games only. Accept twice and verify one membership. A second issuer with the same email cannot reuse it. Direct Media requests fail through web and mobile-facing services. Remove Games and verify subsequent reads, writes and events lose access.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --filter "FullyQualifiedName~AuthenticationEndpointTests|FullyQualifiedName~MobileMembershipTests"
dotnet test ../CSweet.Market/tests/CSweet.Market.IntegrationTests/CSweet.Market.IntegrationTests.csproj
```

Attach root upgrade results, invitation lifecycle, Market ID/entitlement assertions and isolated private recovery evidence. Exercise real authorization policies because some test-host policies grant blanket access. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
