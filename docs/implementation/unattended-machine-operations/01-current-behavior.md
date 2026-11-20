# 01 Current behavior

[Overview](README.md) · [Design](02-design.md)

The [overview](README.md) records the source revisions for the initial review and the follow-up.

## What happens when the Game Engineer is hired

Compute setup runs once per business, is triggered by the hire, and blocks on a person at the
console.

1. `Start-CSweet.cmd` runs the Aspire AppHost in the user's own session. The API, AgentHost,
   WorkerHost, GitHost and the other services are child processes with the user's ordinary,
   non-administrator token.
2. When the hired agent's runtime is queued, `AgentRuntimeManager.EnsureRuntimeQueuedAsync`
   requests compute through `ComputeDefaultsService.EnsureRequestedAsync`
   for an active, ready installation with `compute.provision.v1`
   (`src/CSweet.Infrastructure/Compute/ComputeDefaultsService.cs`). It inserts one
   `ComputeLocalSetup` row for the business in state `Pending`.
3. `ComputeLocalSetupWorker` (`src/CSweet.Api/Compute/ComputeLocalSetupWorker.cs`), a background
   service in the API, polls every 5 seconds. It writes a handoff file (setup id, secret,
   control-plane key, certificate pin, `repositoryRoot`) to
   `%LOCALAPPDATA%\CSweet\ComputeSetup\<id>\handoff.json`.
4. In `ComputeLocalSetupWorker.PassAsync` it starts `powershell.exe -File scripts\Install-ComputeLocalProvider.ps1` with
   `UseShellExecute = true, Verb = "runas"`. **This is the UAC prompt.** The worker then waits for
   the process, so it handles one business at a time.
5. Elevated, the script builds the Linux image (`Ensure-ComputeLinuxImage.ps1`, Packer), builds
   and runs the compute acceptance tests, publishes `CSweet.Compute.HyperV`, and copies it to
   the protected runtime directory selected by `Get-ComputeLocalInstallation`.
6. It runs `configure-local` (machine key, LocalMachine certificate), writes the HKLM guest-socket
   key, registers a SYSTEM retirement task, creates and starts the
   LocalSystem service, and runs `complete-local`. The first business can own the unsuffixed
   `CSweet.Compute.HyperV` service under `ProgramData\CSweet\Compute`. Additional businesses
   can have `CSweet.Compute.HyperV.<business>` services under `ComputeBusinesses`.

### When nobody answers

| Situation | Result | Code path |
| --- | --- | --- |
| Prompt shown, nobody answers | The worker can block at launch or while waiting for exit. Two hours is the handoff lifetime. The expiry sweep needs an available worker pass, so this is not a bound on how long the UI shows Running | `PassAsync`, `Process.Start`, `WaitForExitAsync` |
| Windows refuses the launch | `Failed / administrator_approval_required`. The compute worker groups launch errors under this code, so it does not establish that a person declined | `catch (Win32Exception)` |
| Headquarters has no interactive desktop | `runas` cannot show consent; fails as above | same catch |
| Headquarters is not on Windows | `Failed / local_provider_unavailable` | OS check in `ComputeLocalSetupWorker.PassAsync` |
| Docker upgrade later in the project | Setup returns to `Pending`; a second prompt appears mid-project | `RequestDockerUpgradeAsync` |
| Owner clicks **Repair** on the Compute page | Another prompt | `ComputeDashboardEndpoints` sets `Pending` |
| Each new business | One more prompt; the service is per business | `ComputeLocalInstallation.ServiceName` |

## Hosting modes in the code

"Hosted" means two different things in the repositories. Only one changes where machine work can
happen.

| Notion | Where it lives | State | Effect on machine privileges |
| --- | --- | --- | --- |
| **Hosted account.** First-run choice between Hosted and Private. Hosted signs the owner in with a C-Sweet account at `account.c-sweet.com`; Headquarters still runs on the operator's machine | `src/CSweet.HostedAccount`, `src/CSweet.Api/Auth/HostedSetupEndpoints.cs`, `src/CSweet.UI/Pages/Register.razor`, switch `Installation:LinkingEnabled` | Code written, walkthrough pending ([hosted onboarding](../mobile-connectivity/hosted-onboarding.md)) | None. But the owner can work from a phone through the relay, so nobody may be at the server to answer a prompt |
| **Hosted platform.** C-Sweet runs the customer's Headquarters. Offices are customer-owned, hosted, or both | [`docs/features/hosted-platform/`](../../features/hosted-platform/README.md) (draft, 2026-10-15) and the C-Sweet Cloud design (2026-11-10) | Design only. No hosting-mode flag, bootstrap manifest or managed onboarding exists in `src` | Large. Headquarters runs on Linux with no hardware virtualization. Only Offices have it |
| **Private.** Self-hosted Headquarters with a local administrator | The default path | Working | Today's behavior |

The hosted platform documents already assume the split this plan needs.
`docs/features/hosted-platform/08-hosting-and-migration.md` says hardware virtualization is
required only on the computer that runs agent workloads, so Headquarters can sit on an inexpensive
server without it. Current compute breaks that assumption because it installs a Hyper-V provider
on the Headquarters machine.

## Office platforms

Offices install on three platforms. Each installs a privileged runtime. Some Windows setup and
recovery paths can request elevation again.
Source: `CSweet.Office` `docs/20-security/06-host-privilege-model.md` and
`docs/30-workloads/07-provider-backends.md`.

| Office platform | Privileged service | Backend | Install approval |
| --- | --- | --- | --- |
| Windows | RuntimeHost as the virtual account `NT SERVICE\CSweet.Office.RuntimeHost` in `Hyper-V Administrators`; Maintenance as SYSTEM | `hyperv-gen2` | One elevated installer run |
| Linux (Ubuntu 24.04) | `csweet-office-runtime.service` as root, sandboxed by systemd | `firecracker-kvm` | `sudo csweet-configure-office`, with flags for non-interactive use |
| macOS | launchd runtime service as root | `apple-virtualization` | One installer run |

Two Office facts matter for this plan:

- **Maintenance is already unattended.** `CSweet.Office.Maintenance` runs as SYSTEM, has no
  inbound listener, polls Headquarters, and accepts only `repair` and `upgrade` (SEC-INV-19).
- **Guests have no network device.** Internet access goes through a brokered tunnel that enforces
  the owner's grant and blocked address ranges (SEC-INV-24).

## Elevation and admin-only steps still in the code

Two Headquarters paths launch with `runas`. The configured Windows development launcher serves
first install and can also take precedence over unattended Office maintenance later.

| Step | Where | When it fires | On a non-Windows Headquarters |
| --- | --- | --- | --- |
| Launch the compute installer with `runas` | `ComputeLocalSetupWorker.PassAsync` | First hire of an agent with `compute.provision.v1` per business, Docker image upgrade, Repair | OS check fails the setup with `local_provider_unavailable` |
| Launch the Office installer with `runas` | `ExecutionFleetService.LocalSetupLaunchMethod`, `TryStartWindowsDevelopmentSetup` | First-run setup plus matching local repair and upgrade sessions on the configured development machine | The Windows development route is unavailable. An already enrolled Windows Office may service its own maintenance handoff |
| Build, test and publish as administrator | `scripts/Install-ComputeLocalProvider.ps1` | Inside the compute installer: Packer image build, `dotnet test`, `dotnet publish` from the source tree | Not reachable |
| Create a LocalSystem service per business | Same script, `New-Service` for `CSweet.Compute.HyperV.<business>` | Each new business | Not reachable |
| Machine key and LocalMachine certificate | `src/CSweet.Compute.HyperV/LocalComputeInstaller.cs` | Each compute enrollment | Not reachable |
| HKLM guest-socket registration | `scripts/Register-ComputeGuestService.ps1` | First setup on the machine | Not reachable |
| SYSTEM retirement task | `scripts/Register-ComputeBusinessRetirement.ps1` | Each compute setup | Not reachable |
| Shared image store under `ProgramData\CSweet\ComputeImages` | `src/CSweet.Compute.HyperV/ComputeSharedImageStore.cs` (added 2026-11-17) | Each compute setup | Not reachable |
| Runtime payload replacement | `scripts/Update-ComputeLocalProviderRuntime.ps1` | Runtime update or recovery | Not reachable |
| Compute dispatch signing certificate | `src/CSweet.Infrastructure/Compute/ComputeDispatchSigner.cs` | Every dispatch; auto-created only on Windows | Must be configured by hand or dispatch fails closed |
| Trust the HTTPS development certificate | `Start-CSweet.cmd` (`dotnet dev-certs https --trust`) | First development launch; a Windows trust dialog, not UAC | Not applicable |

Further observations:

- **The shipped compute backend is Windows/Hyper-V.** `IComputeProvider` describes the lifecycle,
  but no production class implements or calls it in this checkout. The live path uses
  `ComputeProviderWorkService.ClaimAsync`, signed dispatch, the provider's local replay journal
  and `ComputeResultReconciler`.
- **The compute network depends on the grant.** `HyperVComputeDriver` requires zero adapters
  for `None`. For `OutboundOnly` it adds a `CSweet Outbound` adapter on the Hyper-V
  `Default Switch` with port ACLs.
- **The elevated script trusts user-writable inputs.** It dot-sources scripts from
  `repositoryRoot`, read from a handoff in the user's `%LOCALAPPDATA%`, and builds a user-owned
  clone as administrator.
- **Compute depends on compiling the unit-test project at setup time.** A compile error in any
  test file stopped compute on 2026-11-10. The build step now has its own failure code,
  `compute_checks_build_failed`, but the dependency remains.
- **Office updates can still prompt on the development machine.**
  [Office automatic updates](../features/office-automatic-updates.md) notes that Windows may still
  require its administrator prompt. Installed Offices from 0.12.0 update through the SYSTEM
  Maintenance service without one; the development launcher path does not.
- **Office has its own interactive entry points.** In `CSweet.Office`, the Configurator
  (`Program.cs`) self-elevates on its normal URI path. `WindowsHyperVFeatureProvisioner` can
  elevate DISM, and `WindowsRuntimeHostProvisioner.LaunchElevated` covers access repair and
  installation. An audit limited to Headquarters would miss these routes.

## What is already in place and reusable

| Piece | Where | Why it matters |
| --- | --- | --- |
| Compute control plane | `src/CSweet.Infrastructure/Compute` (`ComputeBroker`, grants, reconciler, audit) | Independent of where the VM runs; stays |
| Agent compute tools and SDK | `ComputeCapabilityHandler`, `context.Platform.Compute` (SDK 3.42.0) | Agents need no change |
| Signed assignments, fencing, stop receipts, reaper | `CSweet.Office` RuntimeHost | Compute can reuse them |
| Three hypervisor backends | `CSweet.Office` `Runtime.HyperV`, `Runtime.Firecracker`, `Runtime.AppleVirtualization` | Cross-platform compute without a new provider |
| Persistent workload disk | `CSweet.Office.Contracts` 0.13.0 `RuntimePersistentDisk` | Environments can outlive a VM |
| Brokered internet tunnel | `CSweet.Office` `OfficeInternetTunnel`, `GuestInternetProxy` | Replaces the NAT adapter |
| Image manifest, digest verification and provider certification | `CSweet.Office` release and runtime tooling | Reuse verification. Shipping prebuilt compute images for each supported backend and architecture is new release work |
