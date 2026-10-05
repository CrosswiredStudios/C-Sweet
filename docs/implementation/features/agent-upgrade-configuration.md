# Agent upgrades and configuration readiness

`AgentDefinitionService.UpdateAsync` installs an approved package version independently
of inference-provider connectivity. An offline model server must not prevent an agent
package upgrade.

The upgrade merges new manifest defaults with saved definition settings whose keys
remain in the new non-secret configuration schema. Saved provider and model selections
take precedence over manifest defaults. Removed settings are dropped; retained values
are kept for review even when the new schema makes them invalid.

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
