# Agent upgrades and configuration readiness

## Hiring and approved package identity

`InstalledAgentCatalogProvider.SearchAsync` excludes disabled installations with no active
employee. Firing an employee leaves that installation disabled for audit, but its installed
listing must not hide the first-party catalog entry for a fresh hire. Direct
`InstalledAgentCatalogProvider.ResolveAsync` still returns the old disabled instance so stale
references retain their original identity and cannot restart it. Disabled instances attached
to an active employee remain visible as disabled. `AgentCatalogServiceTests` covers firing,
fresh catalog discovery, paused employees and historical-reference resolution.

`HiringService.BuildWorkflowSnapshotAsync` resolves remote catalog candidates against existing
`AgentDefinition` records by normalized repository URL and agent ID. When a definition exists,
the review uses its approved `PackageVersionId`, commit, manifest, configuration schema and
default grants. It does not fetch the repository branch or import a newer package during hiring.
New versions are reviewed through Installed Agents. This also works before the first employee
is hired into a business; `InstalledAgentCatalogProvider` alone only exposes business installations.

`EmbeddedAgentInstallSnapshot.UsesApprovedDefinition` pins this selection. `RevalidateAsync`
rejects package replacement, disabling and grant changes before confirmation, and confirmation
checks the definition response's package ID again before creating the employee. A fresh review
uses the current approved package. New, uninstalled remote agents retain the normal import flow;
explicit local archive reviews retain their immutable archive selection.

The message "This agent already has a different approved version" compares package record IDs,
not only semantic versions. Two commits with identical manifest versions can have different IDs.
A first-party catalog hiring review previously previewed the moving default branch even when a
global definition already existed, then requested `ReuseExistingDefinition` for the new record.
This can produce the error without any direct mutation of an installed agent by Codex.

For an interrupted review made before this fix, dismiss the failed hire and open a fresh hiring
review from the recommendation. After publishing Producer 4.0.5, update Gabriel through Installed
Agents before opening that review. Retrying the old notification reuses its pinned snapshot.
Do not repair this by changing package rows, stored manifests or artifact digests manually.

`HiringServiceTests.ApprovedDefinition` covers approved-package reuse without a remote preview,
retained restricted grants, and rejection of updates, disabled definitions and grant drift.

## Upgrade configuration

`AgentDefinitionService.UpdateAsync` installs an approved package version independently
of inference-provider connectivity. An offline model server must not prevent an agent
package upgrade.

The upgrade merges new manifest defaults with saved definition settings whose keys
remain in the new non-secret configuration schema. Saved provider and model selections
take precedence over manifest defaults. Removed settings are dropped; retained values
are kept for review even when the new schema makes them invalid.

`AgentTokenBudget.Fields` also supplies the platform-owned context/output fields for LLM
packages that do not declare them. Definition and installation upgrades preserve those
settings and employee overrides even when absent from the package manifest. A new package's
smaller default or numeric maximum cannot reduce the user's token budget. Invalid output/context
relationships require configuration repair; `NormalizeRelationalConfiguration` does not
replace token values with package defaults.

`AgentConfigurationRules.IsLocallyReadyAsync` applies required-field, dependency, type,
option, numeric-range, and enabled-provider checks using the manifest and local database.
It does not request the provider's live model catalog. For a built, signed package,
compatible settings make the definition `Available`; missing or incompatible settings
make it `NeedsConfiguration` and unavailable for new hires. An unbuilt package remains
`Building` until its build finishes.

`Agents.TrackAgentBuildAsync` reports a completed package with `NeedsConfiguration` as
installed and awaiting a settings review, instead of claiming that the agent is ready.

`AgentBuildService.UpdateDefinitionBuildStateAsync` and
`AgentDefinitionService.RetryBuildAsync` use the same local readiness checks after source
builds and prebuilt release installation. A successful build does not make incompatible
retained settings valid.

`AgentDefinitionInstallationSynchronizer.SynchronizeAsync` deploys an available definition
to existing hires and preserves configuration overrides whose keys remain in the schema.
When the definition needs configuration or its package is still building, existing hires
remain pinned to their current package. After configuration is repaired, reconciliation
can deploy the available definition.

Explicit configuration saves still use `AgentConfigurationRules.ValidateAsync` with live
supported-model validation through `IModelCatalogClient`. A selected model matching the
provider profile's configured default follows the existing trusted-default rule. Package
updates do not certify that a provider is online or that a saved model is currently
served; actual inference continues to use the runtime's provider error handling.

## Code and regression coverage

- `src/CSweet.Infrastructure/Setup/AgentDefinitionService.cs`: package update and prebuilt retry.
- `src/CSweet.Infrastructure/Setup/AgentConfigurationRules.cs`: local readiness and explicit configuration validation.
- `src/CSweet.Infrastructure/Setup/AgentBuildService.cs`: source-build completion readiness.
- `src/CSweet.Infrastructure/Setup/AgentInstallationConfigurationService.cs`: configuration saves and activation after repair.
- `src/CSweet.UI/Pages/Agents.razor`: build completion messages for definitions needing configuration.
- `tests/CSweet.UnitTests/AgentDefinitionUpgradeConfigurationTests.cs`: offline provider upgrades, retained defaults and employee overrides, incompatible configuration, and explicit model-edit validation.
- `tests/CSweet.UnitTests/AgentBuildJobTests.cs`: successful source builds with valid and incompatible saved settings.
- `tests/CSweet.UnitTests/PrebuiltBundleInstallServiceTests.cs`: prebuilt retry reads persisted configuration and applies full local readiness.

## Employee configuration workspace

`EmployeeDetails` exposes the agent instance editor at `?tab=configuration`.
`EmployeeConfigurationPanel` is shared with the Directory's Configure Agent dialog,
uses the installed agent's schema and conditional fields, and only requests provider
catalogs when the agent declares provider/model settings. Human employees cannot
open this tab; sensitive-view permissions gate rendering and the existing employee
configuration API independently enforces management access.

Saves and per-field/all-default restores use `ExpectedRevision`. A conflict retains
the draft and offers reload; restore actions are disabled while edits are unsaved.
The panel provides field validation, masked secret inputs, catalog retry, and a
single-column layout for mobile and narrow dialogs. `EmployeeConfigurationPanelTests`
and `EmployeeDetailsPageTests` cover the editor and tab routing.
