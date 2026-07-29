# Phase 7 - Business Onboarding

## Goal

Create the first business onboarding flow that captures enough company context for C-Sweet to create default roles, a strategic objective, an empty universal work board, and a default local worker.

## Why this phase matters

After system setup, users need to create the business context that agents will operate inside. This is the start of the actual C-Sweet experience.

## Deliverables

- `/business-onboarding` route.
- Organization creation form implemented in the shared `CSweet.UI` Razor library.
- Initial context capture.
- Default role creation.
- First strategic objective creation.
- Empty default `To Do -> Done` work board provisioning.
- Default local strategy worker registration.
- Redirect to organization command center.
- Shared API client and UI state that can be used by both the Blazor WASM host and the future MAUI Blazor Hybrid host.

## Business onboarding fields

Minimum fields:

```text
Business name
Industry
Stage
Primary goal
Current constraints
Preferred operating style
```

Optional later fields:

```text
Target customer
Revenue model
Team size
Budget
Existing website
Existing tools
Risk tolerance
Brand voice
```

## Stages

Start with a simple list:

```text
Idea
Pre-revenue
Early revenue
Growing
Established
```

## Preferred operating style

This helps shape future agent behavior.

Options:

```text
Conservative and careful
Balanced and practical
Aggressive growth
Experimental
```

## Default roles

When onboarding completes, create these roles:

### CEO

Responsibilities:

- Define company direction.
- Prioritize objectives.
- Approve major decisions.
- Coordinate roles.

Authority level:

```text
ExecutionWithApproval
```

### Operations

Responsibilities:

- Identify operational bottlenecks.
- Create processes.
- Track execution.

### Finance

Responsibilities:

- Estimate costs.
- Evaluate revenue assumptions.
- Identify financial risks.

### Marketing

Responsibilities:

- Define target customer.
- Suggest channels.
- Draft messaging.

### Product

Responsibilities:

- Clarify offering.
- Prioritize features or services.
- Convert goals into deliverables.

## Initial strategic objective

Create one objective from the primary goal.

Example:

```text
Title: Establish a 30-day launch plan
Description: Create a practical operating plan that turns the business goal into immediate actions, risks, owners, and deliverables.
Status: Active
```

## Initial work board

Provision an empty `Company work` board with `To Do` and `Done` columns. C-Sweet
does not create a starter backlog because it cannot assume how an installed agent
or organization will use the work system.

Agents may create appropriate initial work items during their own initialization
flow through the work-management SDK. Those operations require explicit grants
and retain the creating agent's identity and idempotency key.

## Default local worker

Register a worker:

```text
Name: Local Strategy Agent
WorkerType: LocalAgent
ExecutionMode: InProcess or LocalWorkerHost
Capabilities:
  - business-planning
  - operating-plan
  - task-breakdown
  - risk-identification
RequiresHumanApproval: true
```

This worker uses the default chat provider configured during setup.

## API endpoint

```http
POST /api/business-onboarding/complete
```

Request:

```json
{
  "businessName": "Example Co",
  "industry": "Software",
  "stage": "Idea",
  "primaryGoal": "Launch a paid MVP in 30 days",
  "constraints": ["solo founder", "limited budget"],
  "preferredOperatingStyle": "Balanced and practical"
}
```

Response:

```json
{
  "organizationId": "...",
  "createdRoleCount": 5,
  "createdTaskCount": 5,
  "defaultWorkerId": "...",
  "nextRoute": "/organizations/{id}/command-center"
}
```

## Command center destination

After onboarding, redirect to:

```text
/organizations/{organizationId}/command-center
```

Initial command center sections:

```text
Company summary
Primary goal
Roles
Open tasks
Workers
Recent artifacts
Pending approvals
Recommended next action
```

## Web and mobile parity requirements

The business onboarding and command center UI must be host-neutral. Product pages, layouts, form models, validation behavior, and API client calls belong in `CSweet.UI`; `CSweet.App` should only host the shared route for the browser.

Do not place onboarding workflow logic directly in the Blazor WASM host. The future `CSweet.Mobile` project must be able to reference the same `CSweet.UI` route and render an identical experience inside MAUI Blazor Hybrid.

Host-specific behavior should be abstracted behind interfaces when needed:

```text
Local storage / secure storage
Clipboard
File picker
Deep links
Notifications
Device/browser detection
```

The onboarding layout must be responsive enough for:

```text
Desktop browser
Tablet viewport
Phone viewport inside MAUI BlazorWebView
```

## Testing requirements

### Unit tests

- Business onboarding creates organization.
- Default roles are created.
- Initial objective is created.
- Empty default work board is created without starter tickets.
- Local strategy worker is registered.

### Integration tests

- Complete onboarding endpoint returns organization ID.
- Created organization can be loaded.
- Role count is 5.
- Task count is 0 until a user or granted agent creates work.
- Default worker exists.

### UI/manual tests

- User can submit onboarding form.
- Validation messages appear for missing business name and primary goal.
- User lands on command center.
- Onboarding and command center render correctly at phone, tablet, and desktop widths.
- Once `CSweet.Mobile` exists, the same `CSweet.UI` onboarding route renders inside the MAUI Blazor Hybrid host without duplicating page code.

## Acceptance criteria

- [ ] Business onboarding is blocked until first-run setup is complete.
- [ ] User can create first business.
- [ ] Default roles are created.
- [ ] Initial objective is created.
- [ ] Initial tasks are created.
- [ ] Default local strategy worker is available.
- [ ] User lands on command center.
- [ ] Business onboarding and command center product UI live in `CSweet.UI`, not directly in `CSweet.App`.
- [ ] The implementation has no WebAssembly-only dependency in shared onboarding UI code.
