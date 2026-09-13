# Compute MVP

The user's current priority is a usable test release, followed by fortification. The broader compute migration remains the backlog; it is not the MVP release gate.

## Current test entry point

See [direct developer applications](direct-developer-apps.md) for Daniel 1.4.0's general chat-to-code-to-Docker flow and explicit network grants. [Daniel's Linux Hello World test](daniel-hello-world.md) records the earlier fixed-demo acceptance path.

## Acceptance path

1. Ask an approved compute-capable agent for a test instance. Application setup automatically prepares and enrolls local Linux compute, then supplies scoped defaults.
2. Request an ephemeral VM with networking disabled; observe it become ready.
3. Submit a bounded command through the authorized broker and retrieve its output/exit status.
4. Stop/start the environment, then destroy it with an accurate cleanup result.
5. Repeat a provisioning request and confirm it does not create a second VM.

## Current state

- Implemented: generic lifecycle admission/persistence, grants, provider discovery/claims/results, Hyper-V lifecycle driver, and provider-host composition.
- Implemented component: guest command runner, readiness/command wire handler, and provider-side command client. The guest executable uses host-only Hyper-V transport on port 2763: parent binding on Windows and peer CID 2 validation on Linux. It has no TCP or bare-metal execution fallback. Native commands run inside the guest under its runtime account; the VM is the host security boundary.
- Automatic image preparation, provider enrollment, scoped defaults and agent recovery are implemented. Live Hyper-V acceptance is being verified; component tests alone do not establish a working test instance.
- Linux test artifact: `artifacts/compute/mvp/linux-guest`, including the .NET runtime; install inside Ubuntu 24.04 with `scripts/Install-ComputeGuestLinux.sh`. Systemd 254+ is required and Docker is not required.
- Windows test artifact: `artifacts/compute/mvp/guest`. This is a framework-dependent Windows guest build requiring .NET 10. Inside the guest, start `CSweet.Compute.Guest.exe --serve-hyperv`. The artifact is a component for VM setup, not a completed end-to-end MVP.
- Host registry setup remains `scripts/Register-ComputeGuestService.ps1`. Existing provider installation/enrollment instructions apply. No VM, registry entry, service, or live schema was changed while producing this artifact.

## Deferred

Additional providers; snapshots/resize; attached and persistent volumes; public networking/DNS; advanced UI; automated template distribution; stronger in-guest account/token/desktop isolation; expanded recovery and certification work beyond the acceptance path.

Retain the essential MVP boundary: agents receive no host shell, host filesystem, hypervisor controls, or provider credentials. Grants, exact VM ownership, command/output limits, and honest failure/cleanup reporting remain required. Lost command responses are unknown outcomes; the command client must not retry them automatically.

The unfinished restricted-token experiment was removed from the active Windows execution path following the MVP prioritization. Its failing parent-process access test did not demonstrate a VM escape, and stronger in-guest separation remains deferred. Do not describe the process job as a separate security sandbox.

## Automatic setup requirement

All user-facing setup must occur through the application. Trigger UAC when Windows requires elevation; never ask users to run scripts, supply image paths, enter template/workstream UUIDs, or configure provider credentials. A workflow requiring those steps is unfinished MVP work.

The development Office setup now prepares and verifies the compute Linux image automatically under the same UAC session, with setup progress, immutable caching and retry recovery. Twelve PowerShell image/preparation tests pass. Provider enrollment, activation and bounded agent defaults now run automatically through application-owned setup. Live acceptance remains a separate verification step.


## Typed agent SDK (3.42.0)

Agents now use `context.Platform.Compute` for provisioning, lifecycle actions, current-state and operation reads, bounded listing, guest commands and port publication. Shared request/result models and the compute-change event contract replace private agent JSON shapes. The SDK uses the existing capability broker and MCP transport internally; it adds no transport or provider authority to agents.

Daniel 1.3.0 uses this API for the Linux Hello World flow. SDK tests, generated-agent verification, Daniel tests and Core broker/schema compatibility tests cover this integration. Package verification uses sibling SDK references disabled. The SDK package and Daniel package are in `artifacts/compute/mvp/packages`; producing these artifacts does not update a running installation or complete provider enrollment.
