# Phase 6 · Finish administration and remaining parity

**User story.** As an authorized operator, I can administer my running Headquarters from a phone, including a workflow spanning a browser or native file picker.

**Demonstration.** Demonstrate each remaining matrix workflow, its permission boundary and a failure/recovery case against the same Headquarters as the web reviewer.

## Before starting

Requires Phase 5 receipt and transfer foundations. Inspect `Organizations`, `Agents`, `ExecutionFleet`, `SourceControlCallback`, `ManagedPluginReturn`, `PluginManage`, `ToolchainSetup`, `Compute`, `Security` and `SettingsNavigation`. Use disposable businesses, staged provider credentials, a host administrator and a restricted member. Take one matrix action per issue within each sequence.

## Implementation issues, in order

### 1. Business and people

Deliver business create/edit/delete and cleanup, repository onboarding, hiring, team membership, employee operations and memory review. Reuse existing services and durable IDs. For slow deletion or installation show the actual status and recover it after restart.

### 2. Platform-dependent operations

Use the Phase 5 bounded source/sink for plugin bundles. Preview the transferred artifact and authorize install of its exact digest. Expire partial files. Open external consent in the system browser with per-profile transaction state. Query the originating HQ on return. Test cancellation, duplicate callback and switched enterprise.

### 3. Host and business administration

Implement compute provisioning/repair/release, office/fleet operations, source control, agent updates, plugins, model providers and toolchains. Identify the server authorization and permission-denied test before enabling each control. Scope consequential confirmations to the enterprise, business or installation. Preserve existing provider consent and cleanup.

### 4. Reporting, preferences and nested actions

Verify analytics filters, security audit detail, benchmark runs, language, themes, account recovery, provider-specific settings and relevant third-party controls. Walk the drawer, overflow menus and nested pages as a member and an administrator. Check each operation’s physical evidence.

### 5. Reconcile the release inventory

Compare current web routes and action handlers with the Phase 0 inventory. Add newly shipped or missing workflows. A missing supported action for an authorized member blocks general release. Installation and physical host provisioning remain operator prerequisites.

## Acceptance example

Start a provider connection at Northstar, switch to Harbor, then finish in the browser. Harbor receives no account or success indicator. At Northstar recover one binding. Repeat with an expired state and authority removed before the callback.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter "FullyQualifiedName~SourceControlWorkspaceRenderingTests|FullyQualifiedName~AgentInstallationServiceTests|FullyQualifiedName~ConnectionServiceOwnerPanelTests"
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --filter FullyQualifiedName~MobileParityTests
rg -n '^@page ' src/CSweet.UI/Pages
```

Attach admin/member comparisons, file sizes and cleanup results, duplicate-return outcomes, and the reviewed route diff. Follow each repository’s versioning/pack rules for implementation changes to packages. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
