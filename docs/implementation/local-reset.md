# Windows local reset and disk reclamation

Verified: 2026-10-07.

`scripts/Reset-CSweetLocal.ps1` inventories the default Windows development installation
and optionally resets **all local C-Sweet businesses**. It works without the old database.
It is a destructive development reset, not an orphan detector or a retention job for a
live installation. A stopped VM or an old folder is not proof that its contents are unused.

## Why clearing PostgreSQL leaves files behind

`AgentRuntimeStartupCleanupService.CleanupAsync` discovers old workload handles through
`CSweetDbContext.AgentRuntimeInstances`. Deleting the database removes that discovery
source; it does not uninstall Windows services or remove Hyper-V VMs and their disks.
`AgentRuntimeCleanupService.CleanupWorkspaces` and `CleanupBuildLogs` clear stored locators;
they do not recursively delete the corresponding filesystem stores.

Other independent stores are established by `AgentRuntimeManagerOptions`,
`DependencyInjection.ResolveAgentRuntimePath`, `InternalGitStorageOptions`,
`CSweet.AppHost/Program.cs`, and `ComputeLocalInstallation.Root` / `ServiceName`.
`scripts/Install-ComputeLocalProvider.ps1` explicitly preserves an installed provider's
identity, journal, images and workloads when reenrolling after database recreation.

## Preview, then reset

Open **Windows PowerShell 5.1 as Administrator** (PowerShell 7 also works), under the same Windows user who develops/runs
C-Sweet. Elevating as another user selects that other user's LocalAppData. Close C-Sweet,
AppHost/Visual Studio debugging, image builders, setup windows and repository builds.
Back up any project repositories or generated work you want to keep.

From the C-Sweet repository:

```powershell
# Preview the complete default development reset; changes nothing.
.\scripts\Reset-CSweetLocal.ps1 -All

# After reviewing the preview and resolving every blocker:
.\scripts\Reset-CSweetLocal.ps1 -All -Apply
```

`-All` combines the three optional scopes below. Without `-Apply`, even `-All` is read-only.
`-Apply -WhatIf` also prevents changes. The apply run rebuilds the inventory; it does not
trust an old saved plan. No UAC prompt is launched by the script itself.

| Scope | What is selected |
| --- | --- |
| Default | Known `%ProgramData%\CSweet` runtime, Compute/ComputeBusinesses, setup, diagnostics and Office folders; default Office installation folders; user agent archives, snapshots, logs, setup and crash caches; associated local services and VMs |
| `-IncludeBuildOutputs` | `artifacts`, project `bin`/`obj`/`TestResults`, and root `TestResults` in this checkout and sibling `CSweet.*` / `CSweetAgentSdk` Git checkouts; includes generated Office/Isolation base images and recognized temporary image-build VMs/disks |
| `-IncludeUserData` | The entire current user's `%LOCALAPPDATA%\CSweet`, including internal Git repositories, media, provider secrets, certificates stored as files and enrollment state; repository `.csweet` fallback and `%TEMP%\csweet-git` workspaces |
| `-ResetDatabase` | Exactly the local Docker volume `csweet-aspire-postgres` from `CSweet.AppHost/Program.cs`, plus Postgres containers using it |

For example, if you already reset the database and want to preserve provider secrets and
internal project repositories, preview `-IncludeBuildOutputs`, then use the same switches
with `-Apply`. Treat the retained database as disposable after a runtime reset: old
enrollments and runtime records are no longer usable.

The script stops/disables selected services, removes the owned VM registrations, uses the
sibling Office uninstaller when needed, removes service registrations, optionally removes
the Aspire database, and then deletes selected files. Fresh enrollment is required afterward.
Removed base images and build outputs must be downloaded/generated/built again.
If a step fails after changes begin, it stops immediately. Services may remain disabled;
inspect the failure and rerun the preview before continuing. This is not transactional.

## Ownership and refusal rules

`Assert-CSweetResetPath` checks resolved absolute path boundaries and refuses reparse
points/junctions in ancestors. `Get-CSweetResetTree` also refuses links inside targets.
Generated build directories containing Git-tracked files are excluded and printed as
`PRESERVED` (including tracked test fixtures under `TestResults`). Git inspection failures
block apply; a target that acquires tracked files after planning is refused before deletion.

`Get-CSweetResetVmPlan` checks VM configuration paths, disks, DVDs, snapshots and parent
VHD chains. A VM name alone never establishes ownership. A VM outside the reset roots
using a selected disk/image blocks the reset; an owned VM with external writable storage
also blocks it. An external base image of an owned VM may be retained. Host-mounted or
indirectly attached VHDs require explicit dismount/shutdown before retrying.

`Get-CSweetResetImageBuildTargets` handles the temporary Packer layout used by
`../CSweet.Isolation/tools/LinuxImage/CSweet.LinuxImage.psm1` (`New-CSweetLinuxHyperVImage`).
With build outputs selected, it requires a VM under the current user's
`%TEMP%\hyperv<digits>\<vm-name>`, its exact sibling `<vm-name>.vhdx`, no checkpoints,
and an attached existing seed inside selected artifacts. It recognizes these layouts:

- Current `csw-<12 hex>` names with a seed at
  `CSweet.Isolation\artifacts\linux-images\image-<32 hex>\cidata.iso`.
- Legacy `CSweet-Linux-Image-Build-image-<32 hex>` names with that **same run ID** in
  the Isolation seed path (observed in retained local VMs).
- Historical `CSweet-Agent-Guest-Image-Build-<yyyyMMdd-HHmmss>-<8 hex>` names with
  a matching run directory under `CSweet.Office\artifacts\windows-test` or
  `CSweet.SatelliteOffice\artifacts\windows-test`, as used by the older
  `scripts/windows/New-CSweetHyperVTestGuest.ps1` in the SatelliteOffice checkout.

It adds only the VM directory and its disk to the preview,
with the same link checks and measurement as other targets. It never sweeps the entire
temporary Hyper-V folder. All VMs are then checked against the expanded plan, so another
VM sharing this disk still blocks deletion. Close Packer and its Hyper-V plugin before
resetting. Missing seed evidence, custom layouts and external media still require review;
the failure message lists external paths and selected storage references to help diagnose them.
VM planning collects all ownership/inspection failures before stopping, so one preview
reports all VM blockers rather than revealing only the first one on each attempt.

An incomplete Hyper-V/service/filesystem/Docker inventory blocks apply. The preview still
prints readable entries with unknown sizes left blank. Its GiB total is logical file size,
not a guarantee of physical disk space recovered; it excludes unknown directories and
Docker's disk allocation. Windows administrative access is required for protected Compute
folders and Hyper-V. A non-elevated preview is useful but cannot establish a complete plan.

For an MSI-installed Office, first use Windows Installed apps or C-Sweet's Office removal
flow, then preview again. The script refuses to bypass Windows Installer registration.
For development Office installs, it uses
`../CSweet.Office/scripts/windows/Uninstall-CSweetOffice.ps1`; that script owns removal of
Office service privileges, protocol registration, cached artifacts and installed files.

## Explicit exclusions

This script does not reset remote machines, Docker Compose deployments, custom storage
paths, other Windows users, S3 stores, NuGet caches, Docker images/build caches, virtual
switches, Windows certificate-store entries or ASP.NET shared DataProtection keys. It
does not modify repository source, `.git`, user-secrets, `.env` or LLM model stores.
Unknown C-Sweet subdirectories and legacy VMs located outside the selected roots require
separate inspection. These exclusions mean `-All` is all supported default development
scopes, not a machine-wide uninstall of every historical C-Sweet component.

Docker Compose uses different volumes (`docker-compose.yml`), and deleting files inside
Docker may not immediately shrink Docker Desktop's host VHDX. Do not use a global Docker
prune as a substitute for reviewing C-Sweet's resources.

## Verification

`scripts/tests/Reset-CSweetLocal.Tests.ps1` exercises path/junction containment, project
output discovery, shared VHD protection, incomplete inventory and preview/WhatIf gates.
It also covers Packer's split temporary/artifact layout and rejects incomplete ownership
evidence, unexpected disks, checkpoints and other VMs sharing a temporary build disk.
Run it with Windows PowerShell 5.1 or PowerShell 7 and Pester:

```powershell
Invoke-Pester .\scripts\tests\Reset-CSweetLocal.Tests.ps1 -EnableExit
```

The tests use temporary filesystem fixtures and mocked machine operations. They do not
replace an administrator preview or prove a real destructive reset has completed.
