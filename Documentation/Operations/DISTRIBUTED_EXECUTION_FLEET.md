# C-Sweet Office fleet

C-Sweet Headquarters schedules agent builds and runtimes onto independently installed **C-Sweet Offices**. Headquarters retains the `CSweet.ExecutionGateway`, placement, leases, certificate authority, artifact grants, guest broker relay, and `/api/execution-fleet` administration surface. The technical database entity remains `ExecutionNode` for schema stability.

## Connect an office

1. Open Agent Execution setup and choose the machine's operating system.
2. Download the signed package identified by `https://github.com/CrosswiredStudios/CSweet.Office/releases/latest/download/office-release.json`, or by the administrator's private-mirror override.
3. Verify the package signature and install it on the local or remote machine.
4. Create a one-use enrollment in Headquarters and provide it to the installer.
5. Wait for the office to claim enrollment, compare its displayed fingerprint with the machine, and approve it.
6. Confirm the office reports current builder and runtime certification and becomes healthy.

The same workflow applies to the Headquarters machine; AppHost never starts Office. The public bootstrap endpoints are `/api/offices/claim`, `/api/offices/{id}/heartbeat`, and `/api/offices/{id}/certificate`. Fleet administration stays under `/api/execution-fleet`.

## Upgrade

Office never self-updates. Drain the office, wait for zero active assignments, and then install the newer signed package. A v1-to-v1 upgrade preserves identity. The v1 cutover from the legacy daemon revokes its identity and fences active work; create a fresh one-use enrollment.

## Release ownership

Installer construction, image certification, platform smoke tests, signatures, notarization, SBOMs, checksums, and the release manifest live in `CrosswiredStudios/CSweet.Office`. Shared wire contracts live in `CrosswiredStudios/CSweet.Office.Contracts` and are consumed as a pinned NuGet package.
