# 06 Decisions

[Overview](README.md) · [Design](02-design.md) · [Security and risks](05-security-and-risks.md)

## Decided by the CEO (2026-11-10)

| ID | Decision |
| --- | --- |
| D1 | After install, nothing in C-Sweet raises an administrator prompt. Headquarters never runs as administrator or SYSTEM |
| D2 | Privileged work happens only in a separate privileged service. That service is the Office's existing RuntimeHost and Maintenance services, not a new one on Headquarters |
| D3 | All agent workloads run in Offices, including agent-provisioned compute |
| D4 | Approval is given once, at install time, with resource limits |
| D5 | Finish and review the plan before writing code |

## Changes made by the 2026-11-19 review

| # | Update | Why |
| --- | --- | --- |
| 1 | The move is required, not just preferred | A hosted Headquarters runs on Linux, where the current compute setup cannot run |
| 2 | Compute must run on all Office backends, starting with Hyper-V and then Firecracker | Today's provider is Hyper-V only. Hosted Offices are Linux with Firecracker |
| 3 | Compute environments lose their network adapter and use the Office tunnel | Office forbids any network device on a guest (SEC-INV-24) |
| 4 | Keep the Headquarters compute control plane; replace only the provider side | The broker, grants, reconciler, audit and SDK tools are independent of where the VM runs |
| 5 | Approval has two sources: the installer, and plan limits for hosted Offices | A hosted Office has no person at install time |
| 6 | The contract change is a minor version, 0.13.0 to 0.14.0 | Additive under the Contracts versioning rule; signed assignment bytes stay frozen |
| 7 | Environment lifetime is capped at 7 days, then recreated from its persistent disk | Office workloads already have a 7-day maximum; the persistent disk shipped in Contracts 0.13.0 |

## For the team to decide

Record the outcome and date in the last column.

| ID | Question | Recommendation | Outcome |
| --- | --- | --- | --- |
| T1 | How does a compute request reach the Office? | A new Office workload kind, signed like every other assignment. It reuses the signature gate, fencing, stop evidence, reaper and drain rules. Alternative: carry today's signed compute packets over the Office control channel; quicker to wire, but keeps a second signing key, replay journal and verifier inside RuntimeHost | |
| T2 | Which backends ship first? | Hyper-V first for parity with today; Firecracker second as the gate for hosted Offices; Apple Virtualization later | |
| T3 | Which prebuilt compute images do we commit to maintaining? | One Ubuntu 24.04 image with Python, Node, Docker and the browser-test runner, built per backend in the Office release pipeline | |
| T4 | What does an Office allow when the installer or plan says nothing? | Compute off until explicitly allowed | |
| T5 | Do we keep Windows guests? | Drop them from scope. The compute contracts allow a Windows guest; Office images are Linux only | |
| T6 | What happens to compute on existing development installs during the transition? | Keep the old path behind a development switch until Phase 4 passes, then remove it | |

## Smaller questions for implementation

- Does the compute guest program merge into the Office runtime guest, or ship beside it in the
  same image? The design assumes beside it.
- Does an Office update wait for a live environment, or drain and recreate it?
- Is Phase 0 worth doing now for development machines?

## Follow-up source review (2026-11-20)

- **T1 needs two proofs.** Demonstrate a signed VM creation and a subsequently authorized guest
  command. Lose the command acknowledgment, restart the Office and replay the delivery. The
  spike passes when the recorded result is recovered or uncertainty is reported without
  repeating the side effect. Compare both transport options against this same test.
- **T2 and T3 require a feature-by-feature release gate.** Start with the Hyper-V/Linux guest
  profile. Advertise Docker, browser evidence, egress and persistence only on backend and image
  combinations that passed their probes. Require the Linux HQ/Firecracker run for managed hosting.
- **T4 fixes the default.** Also specify who can raise the physical ceiling and how concurrent
  requests share it. A purchase, agent hire or ordinary repair cannot enlarge a customer machine's grant.
- **T5 is an API compatibility decision.** Keep existing Windows guest values readable, surface
  unsupported placement, and keep retained records available for observation and cleanup.
- **T6 needs an ownership and data gate.** Include both service naming schemes, retirement tasks
  and alternate volumes. Require explicit disposition for persistent data and protect shared
  images still in use before retiring a provider.
- **Two wire details are confirmed from source.** `OfficeWorker.DeserializeSpecification` and
  `RuntimeHostProtocolMapper.DeserializeSpecification` read a numeric kind and reject an unknown
  value. `OfficeProviderInventory` in `office_control.proto` uses fields 1 through 15 at
  `79a0991`; 16 is the next available field at that revision. Recheck it when implementing.

## Not verified

The source review did not exercise a running Headquarters, Office or guest. These integration and
hardware questions remain open.

- **Fit of compute operations to the guest protocol.** The contract documents and the proto field
  list were read, not the guest protocol code. Whether execute, archive streaming and port
  streams fit the existing frames, or need new ones, is unconfirmed.
- **Released-version interoperability.** V14 still needs an actual old Office connected to
  the new Headquarters. Source inspection verifies the discriminator, not the deployed handshake.
- **Headquarters execution orchestration internals.** Named from the compute migration notes; how
  it schedules, and how a compute provider plugs into it, was not read.
- **Docker inside a Firecracker guest, and Chromium with WebGL there.** Not tried.
- **Apple Virtualization.** The backend reaps only Runtime workloads today. Compute on macOS
  Offices is out of the first phases.
- **Hosted platform specifics.** No hosting-mode flag, bootstrap manifest or hosted Office
  provisioning exists in code yet, so the hosted rows describe how this plan fits those designs,
  not tested behavior.
- **The Market, ConnectionService and Video Game Director repositories** were not read. Nothing
  found suggests they contain privileged steps.
- **`IsInRole` / `Administrator` matches in the API and Auth code** look like C-Sweet application
  roles, not Windows elevation. Each was not reviewed.

## History

| Date | Event |
| --- | --- |
| 2026-11-05 | Problem reported; first analysis. Options: elevate Headquarters, run as SYSTEM, or a separate privileged service |
| 2026-11-10 | CEO chose the separate privileged service, all agent workloads in Offices, and install-time approval |
| 2026-11-19 | Re-evaluated against the latest code and the hosted and self-hosted shapes; plan updated |
| 2026-11-20 | Written into the repository for team review |
| 2026-11-20 | Follow-up verified current dispatch, launch routes and wire fields, and tightened policy, replay and migration gates. T1–T6 remain open |
