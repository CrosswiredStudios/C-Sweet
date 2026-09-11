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

## Agent release notes

- When changing an agent repository, bump and synchronize its version BEFORE writing release notes.
- Then read the final version from that repository's `csweet-plugin.json` and write `releases/<version>.md` with the same version in its heading. Never put new changes under the previous version.
- If the version changes again, retarget unpublished notes and re-check the final manifest, implementation/package version, filename, and heading before handoff. Preserve published historical notes.

## Asynchronous agent work

- Prefer durable events over agent polling loops for asynchronous platform operations.
- Persist state changes and their notification outbox records atomically. Treat events as wake hints, not authoritative snapshots or execution grants.
- Agents may be offline, miss notifications, or receive duplicates/out-of-order delivery. Provide authorized current-state reads and bounded discovery for wake/reconnect recovery; make resulting effects idempotent.
