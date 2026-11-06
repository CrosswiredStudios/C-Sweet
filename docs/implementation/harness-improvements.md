# Harness improvements — implementation spec and file ownership

Six workstreams, implemented in parallel. **Read this before editing anything.**

## Shared contracts (already committed — do not redefine)

`src/CSweet.Contracts/Agents/AgentPosture.cs`

- `enum AgentPosture { Plan, Manual, Auto }`
- `enum CapabilityRiskClass { Read, Write, Destructive, Outward }`
- `static class AgentAuthority` — `Allows`, `RequiresApproval`, `IsRefused`, `TryParse`

`src/CSweet.Contracts/Agents/AgentGovernanceContracts.cs`

- `AgentPolicyEvent`, `AgentPolicyDecision`, `AgentPolicyWindow`, `AgentPolicyThreshold`
- `CreateDelegationRequest`, `DelegationResponse`

Build against these. If a shape is wrong, report it — do not fork a second version.

## Ground rules

1. **Ownership is exclusive.** Only create/edit files inside your scope. If you need an edit
   elsewhere, stop and report it instead of making it.
2. **Additive by default.** New files over modified files. Never delete or reorder unrelated code.
3. **No package changes.** `Directory.Packages.props` is frozen.
4. **Must be compile-clean.** The solution is .NET 10, nullable enabled, implicit usings on.
   No TODOs that break the build; stub with `NotImplementedException` at the thinnest edge if needed.
5. **No migrations.** If your work needs a schema change, add the EF configuration and report the
   migration as a required follow-up.
6. **Report format** at the end: files created, files edited, integration steps needed, risks.

## Workstreams

### 1 — Capability risk classification
Own: `src/CSweet.AgentHost/Broker/CapabilityRiskCatalog.cs` (new)
Map every brokered capability to a `CapabilityRiskClass` using the existing handler names
(`ArtifactCapabilityHandler`, `AgentMemory*`, `AgentCoordination*`, `AgentOnboarding*`, git/broker
handlers). One static table + `Classify(string)`. Read-only over the handlers; report any capability
you cannot classify rather than guessing.

### 2 — Agent posture
Own: `src/CSweet.Application/Agents/AgentPostureService.cs` (new),
`src/CSweet.Api/Agents/AgentPostureEndpoints.cs` (new),
`src/CSweet.UI/Components/AgentPostureChip.razor` + `.razor.css` (new).
Persist posture per installation (in-memory + follow existing service patterns; report if a table is
needed). Endpoints: get/set posture for an installation. Chip renders Plan/Manual/Auto and the
authority rule from `AgentAuthority`. Do not edit `AgentSession.cs` — report the enforcement hook you
need there.

### 3 — Temporal policy
Own: `src/CSweet.Application/Security/AgentPolicy*.cs` (new),
`src/CSweet.Infrastructure/Agents/AgentPolicyEventStore.cs` (new),
`src/CSweet.Api/Security/AgentPolicyEndpoints.cs` (new).
Implement `RecordAsync(AgentPolicyEvent)`, `CountWithinAsync(AgentPolicyWindow)`, and evaluation of
`AgentPolicyThreshold` rules (`count_within`). Back with the existing audit/DbContext patterns; if a
table is required, add configuration and report the migration. Read `IAuditEventWriter` first and
reuse it where possible instead of duplicating the trail.

### 4 — Delegation packets
Own: `src/CSweet.Domain/Core/Delegation.cs` (new),
`src/CSweet.Infrastructure/Persistence/DelegationConfiguration.cs` (new),
`src/CSweet.Api/Core/DelegationEndpoints.cs` (new),
`src/CSweet.UI/Components/DelegationCard.razor` + `.razor.css` (new).
Entity: id, business id, from/to installation, title, scope, input artifact ids, return contract,
status, timestamps. Endpoints: create, list by business, accept/decline/complete. Card renders a
delegation like a work item. **Only permitted shared edit:** one `DbSet<Delegation>` line in the core
DbContext — report the exact file and line, do not add a migration.

### 5 — Operation tray
Own: `src/CSweet.UI/Components/OperationTray.razor` + `.razor.css` (new).
Generalise the pattern in `AgentHireOperationTray.razor`: a persistent tray listing running operations
(label, progress, state, elapsed), collapsible, accessible (`aria-live` on completion), reduced-motion
safe. Stand-alone component that accepts an operation list via parameter — no service wiring, no
edits to the existing tray.

### 6 — Codemode
Own: `src/CSweet.AgentHost/Broker/CodemodeCapabilityHandler.cs` (new),
`src/CSweet.Contracts/Agents/CodemodeContracts.cs` (new).
A capability that runs a short read-only script over already-granted **Read** capabilities and returns
only stdout. Must: refuse any non-Read capability invocation, cap runtime and output size, deny network
and filesystem access, and be disabled unless explicitly enabled in configuration. Report the
registration point — do not edit the handler registry yourself.

## Integration (done by the orchestrator, not by you)

- Wiring handlers, DbSets, endpoints and UI into registries/layouts.
- EF migrations.
- Broker enforcement calling `AgentAuthority`.

## Definition of done per workstream

- Files exist, compile against the shared contracts, no cross-scope edits.
- A short report listing what integration still needs to happen.
