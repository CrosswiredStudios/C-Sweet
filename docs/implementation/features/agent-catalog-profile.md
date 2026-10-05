# Agent catalog profiles

`MarketplaceAgentCard` opens `MarketplaceAgentProfile` in both the business marketplace
and the settings catalog. The profile uses the listing's branding, shows four capabilities
initially, and reveals remaining capabilities through a native disclosure. The access panel
groups manifest requirements by domain; each request includes its declared purpose, scope,
and exact capability identifier. Event subscriptions/publications, web rules, credentials,
connections, and connection scope sets have separate disclosures.

`AgentCatalogProfileService.GetAsync` reads manifest declarations without importing a
package, creating a hiring workflow, or approving grants. Installed profiles use the
business-scoped installation's pinned manifest. Local profiles use
`LocalDirectoryAgentCatalogProvider.ReadProfileManifestAsync` and reject stale content
references. Repository profiles resolve a catalog-controlled GitHub URL and read a manifest
at a resolved commit. These browse-time declarations can change; the existing hire review
still pins and reviews the actual package used for confirmation.

`AgentCatalogEndpoints` exposes `GET /api/agents/catalog-profile` for global listings and
`GET /api/core/organizations/{organizationId}/agents/catalog-profile` for business listings.
The business route requires active membership. The global route cannot resolve installed
agents. Both take `agentReference` as a query parameter and return
`AgentCatalogProfileResponse`. Failed reads show an unavailable state with retry, never
an empty grant list. Missing prices display **No listed cost**, without implying a license.

`MarketplaceAgentProfileTests` verifies purpose/scope rendering, escaped manifest text,
unavailable states, and business-scoped query encoding. `AgentCatalogServiceTests` covers
read-only repository lookup, repository identity checks, installed package isolation,
and stale local references.
