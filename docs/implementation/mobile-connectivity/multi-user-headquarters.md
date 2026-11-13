# Multi-user Headquarters

Updated 2026-11-12 after independent review (resource ownership, invitation states, existing index, flag lifecycle, audit). **Proposed design; nothing here is implemented.** Decision D1 in the [decision log](decisions.md): Headquarters becomes multi-user now, before mobile members exist. This guide replaces issue 1 and issue 2 of [Phase 2](phase-2-accounts.md) and uses the issue format from the [Phase 0–1 backlog](junior-backlog-phase-0-1.md).

## Where Headquarters is today

| Fact | Source |
| --- | --- |
| One human account. Registration fails with `registration_closed` once an initial administrator exists. | `AuthenticationService.RegisterCoreAsync` |
| One enterprise role, `SystemAdministrator`. Three policies require it: `PluginAdministration`, `SourceControlAdministration`, `HostAdministration`. | `src/CSweet.Api/Program.cs` |
| Every other endpoint needs only an authenticated user (fallback policy). In the `Testing` environment every policy passes. | `src/CSweet.Api/Program.cs` |
| Endpoints take `organizationId` from the route without checking the caller. Example: all of `OrganizationUserEndpoints`. | `src/CSweet.Api/Core/OrganizationUserEndpoints.cs` |
| A person is already represented inside a business: `OrganizationUser` with `EmployeeType.Human`, `ApplicationUserId` and `PermissionLevel` (`Viewer`, `Contributor`, `Manager`, `Owner`). The administrator gets an `Owner` row in every business. | `OrganizationUser`, `AuthenticationService.ReconcileExistingOrganizationsAsync` |
| Realtime already delivers per person: the hub joins `ApplicationUser` and `OrganizationUser` groups. | `src/CSweet.Api/Notifications/AppEventsHub.cs` |
| Fine-grained grants exist for boards, work items and artifacts. | `ScopedActionGrant`, `ScopedActionAuthorizationService` |

So the building blocks exist. What is missing is a second account, a rule that ties a caller to a business, and enforcement of that rule on about 660 endpoints in 73 files.

## Target model

**Person.** `ApplicationUser` (unchanged). A person signs in with a local password or through a linked external identity (see [identity and installation linking](identity-and-installation-linking.md)).

**Enterprise role.** Two values in v1:

| Role | Can do |
| --- | --- |
| Administrator (`SystemAdministrator`, existing) | Everything, in every business. Host settings, plugins, source-control platform, fleet, people and access, device approval. |
| Member (no role) | Only what their business access allows. No host settings. |

More enterprise roles (for example a people-only administrator) are out of scope until someone needs them.

**Business access.** One active human `OrganizationUser` row per person per business. Its `PermissionLevel` decides what they may do there:

| Level | Reads | Writes |
| --- | --- | --- |
| Viewer | Overview, boards, projects, calendar, documents they are granted, analytics, conversations they participate in | Own read markers, own preferences, own personal assistant |
| Contributor | Same | Messages, work items and moves, documents and folders, calendar events, decisions assigned to them |
| Manager | Same, plus employees, memory, security audit | Projects and membership, sprints, board configuration, hiring, team roster, employee runtime, briefings, benchmarks |
| Owner | Same | Business edit and delete, business plugins and provider accounts, compute, repository connection, standing policies, granting business access |

No row means no access: the business does not appear in lists and every route for it returns 404 (not 403, so its existence is not revealed).

**Narrower rules stay where they are.** Conversation participation, artifact shares and `ScopedActionGrant` checks continue to apply on top of the level. The level is the outer gate, never a bypass.

**Personal scope.** Preferences, own devices, own recovery codes, own assistant: the caller's `ApplicationUserId` is the only key. Never accept a user ID from the request.

## How it is enforced

Two layers. The endpoint filter is the outer gate: is this person allowed in this business at this level. The **resource ownership** check is the inner gate: does the record being read or changed actually belong to that business. Both are required. The filter alone is not sufficient, as the next section shows.

```csharp
// src/CSweet.Application/Security/IBusinessAccessService.cs  (proposed)
public interface IBusinessAccessService
{
    // null = no access. Administrators always get Owner.
    Task<OrganizationPermissionLevel?> GetLevelAsync(Guid applicationUserId, Guid organizationId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> ListAccessibleOrganizationIdsAsync(Guid applicationUserId, CancellationToken ct);
}
```

```csharp
// src/CSweet.Api/Auth/BusinessAccessExtensions.cs  (proposed)
group.RequireBusinessAccess(OrganizationPermissionLevel.Viewer);        // route has {organizationId}
group.RequireBusinessAccessVia<TResolver>(OrganizationPermissionLevel.Contributor); // route has only a resource id
group.RequirePersonalScope();      // caller's own data
group.RequireEnterpriseWide();     // deliberately visible to every signed-in member, e.g. auth status
```

Rules:

1. Every endpoint carries exactly one of: `AllowAnonymous`, an administrator policy, `RequireBusinessAccess…`, `RequirePersonalScope`, `RequireEnterpriseWide`.
2. A read uses `Viewer` unless the table above says otherwise. A write uses the level in the table. When unsure, choose the higher level and ask.
3. `RequireBusinessAccessVia<TResolver>` looks the resource up, finds its business, then applies the same check. A missing resource and a forbidden resource both return 404.
4. List endpoints that span businesses filter with `ListAccessibleOrganizationIdsAsync`.
5. Access is evaluated on every request from the database (cached per request only). Removing a row takes effect on the next call. Removing a person's last access or disabling them also rotates their security stamp, which ends cookie sessions and device sessions.
6. Background work and agents do not pass through this filter; they keep their existing grants.

### Resource ownership

A route such as `GET /api/core/organizations/{organizationId}/users/{id}` passes the business filter for a member of that business. Today its handler then calls `OrganizationUserService.GetAsync(id)`, which loads the record by `id` alone; the delete path does the same. A member of Games could therefore read or delete a Media record by putting a Games ID in the URL and a Media record ID after it.

Rule: every query or command that takes a child ID, a body-supplied target or a batch of IDs must include the authorized business in its lookup (`WHERE Id = @id AND OrganizationId = @organizationId`), or resolve the record's business first and check that. A record in another business is treated as missing. This is a change to service code, and it is required.

Every affected workflow gets a **wrong-child test**: an authorized parent business with an unrelated child ID must return 404 and change nothing. Add the same for body-supplied targets and for each item of a batch.

**Passing an actor is not authorizing.** Several services already receive the caller's ID for attribution. That does not mean they check it.

**The safety net.** `EndpointAccessConformanceTests` *(proposed)* enumerates `EndpointDataSource` and fails if any endpoint lacks one of the five markers. Until every endpoint is annotated the test compares against a checked-in allowlist of not-yet-annotated endpoints; the allowlist may only shrink.

**Real policies in tests.** New integration tests run with a test authentication handler and the production policies. Do not rely on the `Testing` environment's pass-everything policies; they hide exactly the bugs this work must find.

**Feature flag.** `MultiUser:Enabled`.

| Stage | Value | Effect |
| --- | --- | --- |
| Introduced (MU-04) | false | A second account cannot be created. Access rules and ownership checks are still enforced for the administrator, which changes nothing they can do. |
| Promoted (MU-12) | true by default | Invitations can be created and accepted. Only when the conformance allowlist is empty and every marked folder has wrong-child tests. |
| Emergency off | false | No new invitations or acceptances. Existing non-administrator members cannot sign in, and their cookie, device and realtime sessions are ended by rotating their security stamps. Their records and access rows are kept. Test this. |

## Data model

All new tables live in `CSweetDbContext` with a migration per issue.

| Table | Columns | Notes |
| --- | --- | --- |
| `MemberInvitations` | `Id`, `SecretHash` (SHA-256 of a 256-bit secret), `Email`, `RequiredIssuer` (null = local account), `EnterpriseRole`, `GrantsJson` (list of `organizationId` + level), `InvitedByApplicationUserId`, `CreatedAt`, `ExpiresAt`, `Status`, `AcceptedByApplicationUserId`, `AcceptedAt`, `Revision` | Unique index on `SecretHash`. Default expiry seven days. |
| `ExternalIdentityLinks` | `Id`, `ApplicationUserId`, `Issuer`, `Subject`, `EmailAtLink`, `LinkedAt` | Unique index on (`Issuer`, `Subject`). Email is display only and never used to match. |
| Access audit | actor, action, target person, target business, before, after, time | Must commit in the same transaction as the change. The existing `AuditEventWriter` opens its own `DbContext` and transaction, so calling it beside a membership change does not give that guarantee. Write the audit row through the same context as the change, or through the outbox. |

`OrganizationUser` is reused as is. A unique index on (`OrganizationId`, `ApplicationUserId`) where `ApplicationUserId` is not null already exists (`CoreConfigurations.cs`); keep it and add nothing. Because of it, re-inviting a person whose row is inactive or archived must **reactivate that row**, not insert a second one.

**Invitation states**

| State | Event | Guard | Next |
| --- | --- | --- | --- |
| Pending | Accept | Secret matches, not expired, caller's identity matches `RequiredIssuer`, inviter can still grant every listed access | Accepted (creates or links the person and the `OrganizationUser` rows in one transaction) |
| Pending | Resend | Caller is an administrator | Superseded; a new Pending row with a new secret |
| Pending | Cancel | Caller is an administrator | Cancelled |
| Pending | Clock passes `ExpiresAt` | — | Expired |
| Accepted | Accept again by the person who accepted it | Same `ApplicationUserId` | Accepted; returns the original result, creates nothing (recovery after a lost response) |
| Accepted | Accept by anyone else | — | No change; fixed error code |
| Cancelled, Expired, Superseded | Anything | — | No change; fixed error code |

## API

| Route | Who | Purpose |
| --- | --- | --- |
| `GET /api/access/members` | Administrator | People, their role and business access. |
| `PUT /api/access/members/{id}/businesses/{organizationId}` | Administrator, or Owner of that business | Set level. Body carries the expected `Revision`. |
| `DELETE /api/access/members/{id}/businesses/{organizationId}` | Same | Remove access. Cannot remove the last Owner of a business. |
| `DELETE /api/access/members/{id}` | Administrator | Remove the person from the enterprise. Cannot remove the last administrator. |
| `POST /api/access/invitations` | Administrator | Create. Returns the secret once. |
| `POST /api/access/invitations/{id}/resend`, `…/cancel` | Administrator | See state table. |
| `POST /api/access/invitations/accept` | Signed-in invitee, or anonymous with local-account activation | Redeem. Rate limited like login. |

## Issues

Levels and sizes as in the Phase 0–1 backlog. MU-01 to MU-04 are the foundation and are senior-led; the annotation issues after that are where juniors do most of the work.

### MU-01 · Access service and filter — M · Senior-led

- **Files.** `IBusinessAccessService` (Application), `BusinessAccessService` (Infrastructure/Security), `BusinessAccessExtensions` and the endpoint filter (Api/Auth).
- **Tests.** Administrator gets Owner everywhere; member with a Viewer row; no row returns null; archived or inactive row returns null; resolver returns 404 for both missing and forbidden.
- **Done when.** `BusinessAccessServiceTests` and `BusinessAccessFilterTests` green, running with production policies.

### MU-02 · Conformance test with allowlist — M · Senior-led

- **Depends on.** MU-01, and `inventory/endpoints.csv` from P0-03.
- **Files.** `tests/CSweet.IntegrationTests/EndpointAccessConformanceTests.cs`, `tests/CSweet.IntegrationTests/Fixtures/endpoint-access-allowlist.txt`.
- **Done when.** Test passes with the allowlist generated from the current endpoints, and fails when a new unannotated endpoint is added.

### MU-03 · Test host with real policies — S · Senior-led

- **Files.** A `MultiUserApiFactory` test fixture that signs in as a named fixture person (administrator, Games-only viewer, Games contributor, Media owner).
- **Done when.** A sample test shows the Games-only viewer gets 404 for a Media route.

### MU-04 · Second account and the feature flag — M · Senior-led

- **Steps.** Add `MultiUser:Enabled`. Keep `RegisterAsync` for the initial administrator only. Add local-account creation through invitation acceptance. Rotate the security stamp on disable and on removal of last access.
- **Done when.** With the flag off, behaviour and existing tests are unchanged. With it on, a second person can be created only through an invitation.

### MU-05 · Annotate one endpoint folder per issue — S to L each · Junior + review

One issue per folder under `src/CSweet.Api` (endpoint file counts in brackets): Core (29), Agents (9), Compute (7), WorkManagement (5), Setup (5), SourceControl (4), Analytics (3), Communications (2), Planning (2), and one combined issue for Auth, BusinessOnboarding, GenAi, Llm, Marketplace, PersonalAssistant and Security (1 each). Core is too large for one issue: split it by five or six files.

- **Read first.** The level table above, and the folder's rows in `inventory/endpoints.csv`.
- **Steps for each endpoint.** (1) Decide which of the five markers applies. (2) Add it at the group if all routes agree, otherwise per route. (3) If the route has no `{organizationId}`, write a resolver or mark it for a senior. (4) Remove the endpoint from the allowlist. (5) Add two tests: allowed person succeeds, person without access gets 404 (or 403 for administrator-only routes).
- Add the business to the service's lookup wherever the route carries a child ID, a body target or a batch (see "Resource ownership"), and add the wrong-child test. A senior reviews every such service change.
- **Do not** change response shapes or route templates in these issues.
- **Split of work.** A senior decides the marker, the level and any resolver for each route and writes them into the issue. A junior applies them and writes the tests.
- **Done when.** The folder has no entries left in the allowlist, every route with a child ID, body target or batch has a wrong-child test, a normal administrator workflow for the folder still passes, and the reviewer has checked every write route's level against the table.

### MU-06 · Filter cross-business lists — M · Senior specifies each list, junior implements

- **Before a junior starts.** For each list a senior writes the contract: what counts and totals mean after filtering, how pagination behaves, whether nested references to other businesses are removed, private-conversation visibility, and what happens when access is removed between pages. Each list comes with a seeded expected response.
- **Steps.** Find list endpoints that return rows from several businesses (start with `GET /api/core/organizations`, attention and approvals lists, search). Apply `ListAccessibleOrganizationIdsAsync`.
- **Done when.** The Games-only fixture person never receives a Media ID from any list; a test asserts this per endpoint.

### MU-07 · Realtime and events — M · Senior-led

- **Steps.** `AppEventsHub.SetCommunicationPerspective` must check business access. Confirm recipient resolution in `ApplicationRealtimeOutboxDispatcher` never addresses a person without access; recheck access at delivery time.
- **Done when.** A person removed from Games while connected receives no further Games event; test with two connected fixture people.

### MU-08 · Invitations — L · Senior-led, juniors take the endpoints and tests

- **Files.** `MemberInvitation` entity, migration, `IMemberInvitationService`, endpoints under `src/CSweet.Api/Access/`.
- **Tests.** Every row of the state table; two concurrent accepts create one person; inviter demoted before accept makes accept fail; wrong issuer fails; ten wrong secrets trip the rate limit.

### MU-09 · People and access page — L · Junior + review

- **Files.** `src/CSweet.UI/Pages/PeopleAccess.razor` *(proposed)*, entry in `SettingsNavigation.razor`, API client `AccessApiClient`.
- **Steps.** Follow the **Settings → People & access** row in [ui-design.md](ui-design.md): member list, invitation list, invite form with a review step, edit access, remove with a named confirmation. On a revision conflict reload and show the current values. All strings localized.
- **Done when.** Rendering tests for empty, populated, conflict and non-administrator (page hidden and API returns 403).

### MU-10 · Hide what a member cannot use — M · Junior

- **Steps.** `NavMenu`, `SettingsNavigation` and `BusinessSwitcher` show only destinations the signed-in person can open. Add the person's enterprise role and per-business level to the auth status response so the UI does not guess. The server check remains the authority; hiding is for clarity only.
- **Done when.** Rendering tests for administrator and Viewer; a Viewer who types an administrator URL sees the not-authorized state, not a broken page.

### MU-11 · Upgrade and last-owner rules — S · Senior-led

- **Done when.** Upgrading an existing single-administrator installation changes nothing visible; the last administrator and the last Owner of a business cannot be removed or demoted, including when two demotions race; tests cover both.

## Open questions

- **MU-Q1.** The level table lets a Contributor decide approvals assigned to them, but the current approvals dashboard rejects anyone below Manager (`ApprovalDashboardService`). Either approvals stay Manager-and-above, or a recipient-scoped approval inbox is built for Contributors. Owner decision.
- **MU-Q2.** Recovery when the initial administrator is unavailable: root recovery and recovery-code regeneration currently require that one person. Who else may recover an installation?
- **MU-Q3.** Linked invitations: an invitation is addressed to an email, but identity is bound by issuer and subject. Define the pending-invitation-to-verified-recipient step (and the directory entry for a recipient who has no account yet) before IL-06 and MU-08 fix a schema. Legacy Market accounts must re-prove their mailbox before claiming an email-addressed invitation.

### MU-12 · Switch on — S · Senior-led

- **Done when.** Allowlist is empty, `MultiUser:Enabled` defaults to true, and the Phase 2 acceptance example passes: invite Maya to Games only, accept twice and get one membership, direct Media requests fail from web and from mobile-facing services, remove Games and reads, writes and events stop.
