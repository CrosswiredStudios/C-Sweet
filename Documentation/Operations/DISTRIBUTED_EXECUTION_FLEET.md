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

### macOS Office

In Agent Execution, choose **This machine** for a local Mac or **Another machine → macOS** for a separate Mac. Use the signed, notarized Office package for the target Mac (arm64 or x64). After installing it, run the configuration command displayed by Headquarters in Terminal and enter the one-use code at its prompt. For a remote Office, use an HTTPS Execution Gateway address reachable from the Office machine.

For a fleet targeting Apple silicon, select Linux arm64 builder and runtime guests before starting Headquarters. Use arm64 agent bundles, or build the agent from source on the matching certified Office. Select x64 when targeting an Intel Mac or an x64 Office on another machine.

```sh
export CSweet__AgentRuntime__BuilderGuestArchitecture=arm64
export CSweet__AgentRuntime__RuntimeGuestArchitecture=arm64
./Start-CSweet.command
```

Mac Office runs agent guest VMs through Apple Virtualization.framework. Generic Compute environments currently require a Windows Hyper-V Office. A Mac Headquarters can connect to that Office over its HTTPS Execution Gateway.

The hosted Windows and Ubuntu development bundles do not supply Mac guest images. If your deployment has no signed Mac package, connect Headquarters to a certified Office on another host. [Mac Office installation](https://github.com/CrosswiredStudios/CSweet.Office/blob/main/docs/40-operations/03-installation-macos.md)

## Upgrade

Office never self-updates. Drain the office, wait for zero active assignments, and then install the newer signed package. A v1-to-v1 upgrade preserves identity. The v1 cutover from the legacy daemon revokes its identity and fences active work; create a fresh one-use enrollment.

## Release ownership

Installer construction, image certification, platform smoke tests, signatures, notarization, SBOMs, checksums, and the release manifest live in `CrosswiredStudios/CSweet.Office`. Shared wire contracts live in `CrosswiredStudios/CSweet.Office.Contracts` and are consumed as a pinned NuGet package.
