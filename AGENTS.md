# C-Sweet contributor instructions

## Cross-repository package versioning

- If a change edits `CSweet.WorkManagement.Contracts`, increment the package version in
  `../CSweet.WorkManagement.Contracts/src/CSweet.WorkManagement.Contracts/CSweet.WorkManagement.Contracts.csproj`
  in the same change. Do not rely on local project references, because they can hide a stale
  published package version.
- If a change edits `CSweet.Agent.SDK`, increment the package version in
  `../CSweetAgentSdk/src/CSweet.Agent.SDK/CSweet.Agent.SDK.csproj` in the same change.
- If a change edits `CSweet.Office.Contracts`, increment the package version in
  `../CSweet.Office.Contracts/src/CSweet.Office.Contracts/CSweet.Office.Contracts.csproj`
  and update the `CSweet.Office.Contracts` pins in both C-Sweet and
  `../CSweet.Office`. Verify both consumers with sibling project references disabled.
- Use semantic versioning: patch/build for compatible maintenance, minor for additive public APIs,
  and major for breaking public APIs, unless the user requests a specific version.
- Keep downstream package pins and each package repository's documented/template/test versions in
  sync. Before handoff, build/test and pack every changed package and verify the `.nupkg` version.
