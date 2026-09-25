# Shared Office and compute image provisioning

Office and generic compute now call the same installer-owned `CSweet.LinuxImage` service in the sibling Isolation repository. One Packer template handles Ubuntu installation, Generation 2 Secure Boot, root-disk sizing, payload transfer, and image export. Office and compute supply separate software profiles. This reuses the Office build approach without modifying existing Office disks or bringing Office's scratch-disk broker into a compute VM.

Users do not run a script. The application’s local Office setup flow invokes the internal Linux preparation step under its existing UAC approval and reports progress in the same setup screen. A verified cached image is reused; changed guest inputs or damaged cache contents cause a fresh build without replacing existing images.

The internal build step publishes the Linux compute guest, prepares an Ubuntu 24.04 image containing Python 3, and produces:

- `artifacts/compute/mvp/images/ubuntu-compute.vhdx`
- `artifacts/compute/mvp/images/ubuntu-compute.vhdx.build.json` with its SHA-256 build receipt.

An existing output is preserved. To rebuild, supply a different `-OutputPath`. `-SwitchName`, `-UbuntuVersion`, `-PackerVersion` and `-IsolationRoot` are installer options. The build VM temporarily uses the selected network switch to install packages. This does not change runtime compute networking permissions.

The compute setup worker automatically installs and enrolls the provider and registers the verified template; see [the MVP](compute-mvp.md). The build command does not register or certify a template, migrate a live database, update a running agent, or launch Daniel's workload. Those retain their existing authorization and installation paths.

Office's existing setup entry point calls the same module and preserves its progress heartbeat and returned image-path contract. Its cache fingerprint now includes the shared builder, its own adapter, and the toolchain guest. Legacy Office Packer/seed source files remain available for compatibility; the current adapter uses the shared implementation.

Validation: eight shared builder/adapter tests and 55 Office onboarding tests pass; both image profiles pass Packer validation; four Linux provisioning scripts pass Bash syntax checks. Hyper-V guest boot and Daniel's live application remain hardware acceptance steps requiring elevation.

## Disk ownership and deletion

`Ensure-ComputeLinuxImage.ps1` keeps every successfully built, fingerprinted VHDX under
`artifacts/compute/mvp/images`; `current-image.json` selects only the newest verified
image. These are shared provider templates, not business-owned VM disks. Deleting a
business must not remove a template still referenced by another installation or an
active VM. The cache currently has no automatic retention/pruning policy, so repeated
guest changes can accumulate multi-gigabyte images. Before removing an older image,
check every protected compute `catalog.json` and Hyper-V disk parent reference; the
current-image receipt alone is not proof that an older image is unused.

Per-environment private disks live in the provider's protected workload directory.
The compute provider removes them only after a confirmed destroy. Business deletion
now refuses to purge compute records while any environment lacks
`TeardownConfirmedAt`, preserving the information needed to finish cleanup. Office
RuntimeHost similarly removes per-workload disks on destroy; its reaper also retries
expired builder workloads after the broker lease ends. Business deletion does not currently uninstall the host-wide Office service or the per-business compute provider installation; their protected installation data needs a separate uninstall lifecycle.
