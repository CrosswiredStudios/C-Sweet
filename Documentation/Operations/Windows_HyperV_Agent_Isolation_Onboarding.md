# Windows Hyper-V agent isolation onboarding

## Guided setup from a source checkout

After cloning the repository, double-click `Start-CSweet.cmd` at its top level. Developers may instead start AppHost from their IDE. Docker Desktop is required because AppHost provisions the PostgreSQL database through its Linux container engine. The launcher checks for the required .NET SDK and Docker, attempts to start Docker Desktop when necessary, waits for its engine, starts the complete application, and opens the browser.

Docker is trusted application infrastructure, not the untrusted-agent security boundary. On the first-run **Agent Execution** page, choose **This machine**, then **Install Agent Host**, and approve the standard Windows administrator prompt. C-Sweet detects a source checkout and launches and monitors the complete development preparation without requiring the user to find or run a repository script.

The preparation performs these operations:

1. Enables the full Microsoft Hyper-V feature when necessary, without restarting Windows automatically.
2. Installs the Microsoft OpenSSH Client capability when the temporary image-build key requires it.
3. Downloads HashiCorp Packer and Ubuntu Server from their official HTTPS release endpoints and verifies their published SHA-256 checksums.
4. Builds a Secure Boot-enabled Ubuntu Generation 2 VHDX containing the self-contained C-Sweet guest broker, the reviewed .NET builder, and the .NET 10 SDK.
5. Boots the image through the real C-Sweet Hyper-V helper with no virtual network adapter and a disposable scratch VHDX.
6. Runs the in-guest isolation probe over Hyper-V sockets and creates certification evidence only when every check passes.
7. Creates a developer-only image-signing certificate, signs the exact tested VHDX, packages RuntimeHost, and installs the versioned Windows service.

The first run downloads several gigabytes and builds Ubuntu, so it can take tens of minutes. C-Sweet keeps the onboarding page available and rechecks automatically. If Hyper-V was newly enabled, save your work and restart when the page asks. Reopen C-Sweet after Windows starts and choose **Install Agent Host** again if setup asks you to resume. C-Sweet never initiates that restart.

The underlying script remains available for CI, recovery, and advanced diagnostics:

```powershell
.\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1
```

The guest image is cached at `artifacts/windows-runtime/source/csweet-agent-guest.vhdx`. Later runs reuse it. Use `-RebuildGuest` to rebuild it, `-SkipInstall` to produce but not install the development payload, or `-SwitchName 'Your Hyper-V Switch'` when the host does not use `Default Switch`.

After a successful install, the onboarding page loads the protected key and detects RuntimeHost without an application restart. Agent builds and runtime launches discover the active immutable guest-image identity from RuntimeHost's certification, so users do not need to copy an image version or SHA-256 digest into application configuration. Deployments may still configure an expected digest as an explicit pin. All Hyper-V, RuntimeHost, signed-image, and certification checks should pass. This workflow creates development certification and a current-user development signing key for local testing only; production packages must use the controlled release signer and the complete release certification suite.

When the required guest certification suite changes, an older otherwise healthy RuntimeHost is shown as needing preparation again. Choosing **Prepare secure agent runtime** rebuilds, tests, signs, and installs the current image; users should not delete VMs, edit hashes, or clear the application database. The current builder downloads the exact approved GitHub commit and NuGet packages only through the authenticated host/guest broker. Builder and runtime VMs have no virtual network adapter, and every disposable VM is removed after completion or failure.

## Progress and time estimates

The elevated development bootstrap and packaged installer publish the same versioned progress document beneath `%ProgramData%\CSweet\Setup`. The directory grants the C-Sweet control-plane user read access while only System and elevated administrators can modify progress. C-Sweet validates file location, size, schema, timestamps, bounded messages, and job identity before displaying it.

The onboarding page rechecks every five seconds and shows the current phase, overall percentage, elapsed time, and an estimated remaining range. Long and variable work such as the first Ubuntu guest build uses a conservative range rather than a false exact time. Cached guest-image runs automatically start at a later milestone with a shorter estimate. Download, build, certification, signing, packaging, service installation, restart-required, completion, and failure states are all reported through the same provider-neutral onboarding response.

## What the user experiences

First-run setup includes an **Agent Execution** step. Its compact progress experience summarizes installation and health while advanced prerequisite details remain available under **Settings > Agents > Runtime**:

- Supported Windows edition.
- SLAT, firmware virtualization, hardware DEP/NX, and at least 4 GB physical memory.
- Hyper-V Windows feature and running hypervisor state.
- Restart required specifically to finish Hyper-V enablement.
- Authenticated `CSweet.RuntimeHost` local service connectivity.
- Native Hyper-V helper, signed VHDX, pinned signer certificate, and certification evidence.
- Current provider certification.

If Hyper-V is disabled and the application is running in an interactive Windows session, **Enable Hyper-V** opens the normal Windows UAC prompt and launches this fixed Microsoft operation:

```powershell
DISM /Online /Enable-Feature /All /FeatureName:Microsoft-Hyper-V /NoRestart
```

C-Sweet never restarts Windows automatically. The screen tells the user when to save work and restart. If the application is hosted as a non-interactive service, it displays the equivalent administrator command instead.

When a release contains a validated Windows runtime payload, **Install secure agent runtime** opens a UAC prompt and runs the bundled installer. The installer verifies every payload file against `runtime-manifest.json`, installs a versioned RuntimeHost and helper, creates protected artifact and VM-data directories, generates the local 32-byte authentication key, registers the Hyper-V socket service, installs and starts the Windows service, and applies explicit ACLs. C-Sweet monitors readiness and loads the newly created key without requiring an application restart.

If a completed RuntimeHost installation cannot be reached from the current Windows account, onboarding presents **Repair secure agent runtime**. The short UAC-assisted repair updates only the protected pipe, key, and artifact permissions, restarts RuntimeHost, and rechecks readiness automatically. It does not rebuild the certified guest image. If the installed service or protected state is missing, repair fails closed and onboarding offers the full preparation flow.

Initial setup cannot continue until at least one Agent Host is installed, connected, approved, and healthy. The server rechecks this requirement when completing the step, and agent execution remains fail-closed whenever healthy capacity is unavailable.

The same panel remains available after setup at **Settings > Agents > Runtime**, so upgraded installations and administrators can recheck or remediate the host later.

## Release payload and installer

Create a release payload with `scripts/windows/New-CSweetWindowsRuntimePayload.ps1`. It publishes self-contained RuntimeHost/helper binaries and packages the supplied signed VHDX, detached signature, signing certificate, and certification evidence. `src/CSweet.Api/CSweet.Api.csproj` includes that payload beneath `windows-runtime/payload` when `artifacts/windows-runtime/payload/runtime-manifest.json` exists.

The onboarding action runs `scripts/windows/Install-CSweetRuntimeHost.ps1`, which performs privileged, machine-wide installation work once so the main application does not need administrator rights afterward:

1. Publish and install `CSweet.RuntimeHost` as the `CSweet.RuntimeHost` Windows service.
2. Publish `CSweet.AgentRuntime.HyperV.Helper.exe` into an administrator-writable, standard-user-read-only installation directory.
3. Store the signed base VHDX, its detached signature, signer certificate, and certification evidence under a protected application-data directory.
4. Generate a random RuntimeHost authentication key of 32 bytes, store it with a current-user/System/Administrators ACL, and let both processes load that same file.
5. Register the C-Sweet Hyper-V socket service identifier and configure `CSWEET_HYPERV_BROKER_SERVICE_ID` for RuntimeHost.
6. Set `CSWEET_HYPERV_DATA_ROOT` to a protected directory writable only by the RuntimeHost service identity and administrators.
7. Configure the complete `CSweet:AgentRuntime:Providers:HyperV` section shown in `src/CSweet.RuntimeHost/appsettings.json`.
8. Start RuntimeHost. Certification evidence must already be bound to the exact provider version, host OS/architecture, guest image digest, broker protocol, suite version, and validity window.

The installer refuses a payload with an unexpected schema, unsafe relative path, duplicate/case-colliding path, missing file, or SHA-256 mismatch. Releases must sign the outer C-Sweet installer/package through the normal Windows release-signing pipeline; the payload manifest is an integrity manifest inside that trusted package, not an independent publisher identity.

Do not grant ordinary users write access to the helper, VHDX, signature, signer certificate, certification evidence, RuntimeHost configuration, or VM instance directory.

## Fail-closed diagnostics

The provider remains unavailable if any one of these checks fails:

- Helper, VHDX, signature, certificate, or evidence file is missing or not addressed by an absolute path.
- VHDX or evidence SHA-256 differs from configuration.
- Certification evidence fields do not bind to the exact provider/image/protocol/certification configuration.
- Detached VHDX signature does not verify with the pinned RSA or ECDSA certificate.
- Signer certificate is outside its validity period or its thumbprint does not match configuration.
- Hyper-V socket service registration is missing.
- RuntimeHost cannot authenticate the local control plane.
- Provider certification is missing, expired, revoked, or bound to another provider/image/protocol build.

The application does not fall back to Docker, WSL, or a host process.

## Authoritative Windows references

- [Install Hyper-V in Windows and Windows Server](https://learn.microsoft.com/windows-server/virtualization/hyper-v/get-started/install-hyper-v)
- [System requirements for Hyper-V](https://learn.microsoft.com/windows-server/virtualization/hyper-v/host-hardware-requirements)
- [Create a virtual machine in Hyper-V](https://learn.microsoft.com/windows-server/virtualization/hyper-v/get-started/create-a-virtual-machine-in-hyper-v)
- [Make your own Hyper-V integration service](https://learn.microsoft.com/windows-server/virtualization/hyper-v/make-integration-service)
