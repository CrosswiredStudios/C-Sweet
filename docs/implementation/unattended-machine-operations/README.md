# Unattended machine operations: review set

**Status: proposed design. Implementation and hardware acceptance are pending.**
Initial source review on 2026-11-19, with a follow-up on 2026-11-20.

Sources read: `csweet` at `c92a581f`, `CSweet.Office` 0.15.2 at `c5f3521`,
`CSweet.Office.Contracts` 0.13.0 at `79a0991`, `CSweet.Isolation` at `2838dda`.
The `csweet` working tree had about 20 uncommitted files at review time; the review reflects the
files on disk. The follow-up checked the live dispatch, Office launch routing and disk contracts
against `csweet` at `f3f72f75` with local edits, `CSweet.Office` at `b4cb8ce`, and the same
Office.Contracts revision.

## The problem

Hiring an agent that can provision compute (for example the Game Engineer) makes the Headquarters
API launch a PowerShell installer with `Verb = "runas"` on the Headquarters machine. Windows shows
a UAC prompt in the middle of a project. If nobody answers, setup may remain blocked.
The handoff expires after two hours, but the worker must run another pass
to record that failure. An image upgrade, **Repair**, or setup for another business can prompt again.

On a Headquarters that is not running on Windows the same code fails at once with
`local_provider_unavailable`. Automatic local compute setup is therefore unavailable on the
proposed Linux Headquarters. This check says nothing about an independently enrolled provider.

## The proposal

Move agent-provisioned compute out of Headquarters and into Offices.

- Headquarters never needs administrator rights and never sets up a machine.
- Headquarters keeps the compute control plane: agent tools, `ComputeBroker`, grants, quotas,
  operations, reconciliation, audit and spend control.
- The Office's existing privileged service (RuntimeHost) creates compute environments next to
  agent runtimes, on Windows, Linux and macOS.
- Approval is given once: in the Office installer for a self-hosted or customer-owned Office, or
  from the customer's plan for a hosted Office.

The approval applies to each Office machine. On managed hosts the operator supplies it during
provisioning. In both cases Headquarters still checks each business's current grants. An
unattended same-machine deployment also needs Headquarters to start at boot under an ordinary
service identity so users can reach it after logoff or reboot.

| Deployment shape | Today | After this change |
| --- | --- | --- |
| Private Headquarters on Windows, Office on the same machine | UAC prompt on first hire, on image upgrade, on repair, and for each new business | One approval when the Office is installed; none later |
| Private Headquarters, Office on another machine | Compute still installs on the Headquarters machine, with the same prompts | Compute runs on the Office machine; the Headquarters machine needs no Hyper-V |
| Headquarters signed in with a C-Sweet account ("Hosted" at first run) | Same as private; the owner may be on a phone and never see the prompt | Same as private after the change |
| Headquarters hosted by C-Sweet, customer-owned Office | Automatic setup fails on Linux Headquarters | Compute runs on a certified customer Office within its install-time policy |
| Headquarters hosted by C-Sweet, hosted Office (Linux) | Automatic setup fails on Linux Headquarters | Compute runs within operator and customer plan limits after the Firecracker gate passes |

## Reading order

| Document | Read it for |
| --- | --- |
| [01 Current behavior](01-current-behavior.md) | What the code does today, step by step, in each deployment shape |
| [02 Design](02-design.md) | The target design, the contract change, approval and limits |
| [03 Work plan](03-work-plan.md) | Phases with gates, and work by repository |
| [04 Verification](04-verification.md) | What must be shown on real hardware |
| [05 Security and risks](05-security-and-risks.md) | What each component can do, invariants touched, risks |
| [06 Decisions](06-decisions.md) | Decisions made, decisions requested from the team, and what was not verified |

For a product review read this page and 06. For a technical review read 01, 02, 05, then 03 and 04.

## Summary of what the team must decide

The product recommendations are T4–T6. The separate maintenance fix can ship against today's
Office. For compute, decide T1 from the restart experiment before changing the wire contract,
then settle T2 and T3 before backend implementation. [Decision record](06-decisions.md)

- **T1** How a compute request reaches the Office.
- **T2** Which Office backends ship first.
- **T3** Which prebuilt compute images we commit to maintaining.
- **T4** Default limits when the installer or plan says nothing.
- **T5** Whether to drop Windows guests from scope.
- **T6** What happens to compute on existing development installs during the transition.

## Related documents

- [Compute MVP](../compute-mvp.md), [compute administration](../compute-administration.md),
  [compute migration](../compute-migration.md), [shared Linux images](../shared-linux-images.md)
- [Office assisted installation](../features/office-assisted-installation.md),
  [Office automatic updates](../features/office-automatic-updates.md)
- [Hosted platform](../../features/hosted-platform/README.md)
- [Hosted onboarding](../mobile-connectivity/hosted-onboarding.md)
- `CSweet.Office`: `docs/20-security/06-host-privilege-model.md`,
  `docs/20-security/11-security-invariants.md`, `docs/30-workloads/`
- `CSweet.Office.Contracts`: `docs/workloads.md`, `docs/versioning.md`
