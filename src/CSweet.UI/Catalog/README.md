# Bundled plugin discovery

`plugins.json` supplies the Find plugins cards: identity, name, localized description, category,
GitHub repository name, fallback icon key and optional bundled brand-logo path. `PluginCatalog`
loads it as an embedded resource, so changes require rebuilding and releasing the UI; this is not
a live `appsettings.json` override or a remote marketplace feed.

Repository URLs currently use the CrosswiredStudios GitHub organization. Brand artwork lives in
`wwwroot/images/brands` and renders through `PluginCatalogIcon`, including on Installed cards.
Logo paths must be local bundled assets, not arbitrary remote URLs or executable markup.
YouTube's red play mark identifies the integration; it does not imply endorsement.
[YouTube brand resources](https://brand.youtube/).

Discovery metadata grants no authority. Review fetches the repository's current manifest, and
installation still requires administration authorization, exact grants and package review.
Provider sign-in and consent happen separately after installation.

Service/connector installation is enabled by default and independent of the imported-agent runtime
opt-in. `PluginManagementEndpoints` reports this availability, and `AgentInstallationService`
applies `EnableImportedAgents` and the community-agent always-on opt-in only to agent workloads.
Reviewed service plugins can keep their declared always-on behavior without changing those agent
settings. Existing agent settings are not rewritten; resource limits and grants still apply.
