# 05 Security and risks

[Overview](README.md) · [Design](02-design.md) · [Decisions](06-decisions.md)

## Security review

The change removes a privileged path from Headquarters and adds one workload kind to an existing,
already-guarded one. The installer path is retired only after migration. The new guest commands,
archive transfers and remotely accessible publications each need their own authorization tests.

| Component | Can do today | Can do after |
| --- | --- | --- |
| Headquarters API | Launch an elevated installer that runs scripts and builds from a user-writable source tree | Nothing privileged. Signs assignments, as it does for runtimes |
| Compute provider on the Headquarters machine | One LocalSystem service per business that both talks to the network and controls Hyper-V | Removed |
| Office Node | Network only; cannot touch the hypervisor | Unchanged; also relays compute operations |
| Office RuntimeHost | Creates VMs only for signed, fenced assignments; no network listener | Unchanged; also accepts the new kind within the Office's compute policy |
| Office Maintenance | Accepts only `repair` and `upgrade` from Headquarters | Unchanged |
| Agent | Uses granted compute tools; no host shell, paths or credentials | Unchanged |

### What gets better

- The elevated installer that dot-sources scripts from a path in a user-writable handoff, and
  runs `dotnet test` and `dotnet publish` as administrator, is deleted.
- Compute stops running as LocalSystem. On Windows it runs under a virtual account that holds
  only `Hyper-V Administrators`.
- The privileged service no longer talks to the network. Office's rule holds for compute too: the
  part with network access has no privilege, and the part with privilege has no network access.
- Compute VMs lose their network adapter. Internet access is enforced by destination at the
  Office, the same way as for agent runtimes.
- Reuse the Office assignment trust root and fencing gate. Keep durable operation receipts and
  reservations for guest actions. The assignment replay ledger alone covers VM creation.

### Office invariants touched

| Invariant | Effect |
| --- | --- |
| SEC-INV-07 (no bypass of the authorization gate) | The new kind must go through the same gate. No separate entry point |
| SEC-INV-19 (Maintenance stays bound to the install tree) | Preserve the repair/upgrade allowlist and the installed compute ceiling. Existing Office credentials cannot enable compute or raise it |
| SEC-INV-20 (fixed helper surface) | The helper gains compute-image creation; needs the same protocol-version treatment as the persistent disk |
| SEC-INV-24 (no network adapter, no host listener) | Kept. Today's wording says compute workloads do not inherit runtime internet policy; that sentence must be rewritten, because compute will use the same tunnel under its own grant |
| New invariant | A compute environment is created only when the Office's administrator-owned policy allows compute and the request fits its ceilings |

### Hosted platform notes

- Compute on a hosted Office leaves through the same per-customer outbound path as agent
  runtimes, so the platform's blocked ports and rate limits apply in one place.
- Published test ports are served by the customer's Headquarters. Public sharing remains a
  separate, later decision in the hosted design.
- A customer-owned Office linked to a hosted Headquarters is still administered by the customer.
  C-Sweet cannot raise its compute limits.

## Risks and mitigations

| Risk | Why it matters | Mitigation |
| --- | --- | --- |
| Compute semantics do not map cleanly onto Office assignments | Compute has start, stop, restart, generations and per-operation dispatch. Office workloads are created, leased and destroyed | Decide T1 first. Prototype provision, one command and destroy on Hyper-V in Phase 2 before porting the rest |
| Docker and package installs through the tunnel instead of a NAT adapter | The guest sees a TUN device and a SOCKS5 bridge, not ordinary networking. Docker image pulls and some tools may behave differently | Test the Software Developer's Docker flow and `npm install` in Phase 2. Preload the browser-test image into the compute image, as today |
| Browser tests with WebGL on Firecracker | Today's probe ran on Hyper-V only | Treat as a Phase 5 gate item. If it fails, restrict browser tests to Offices that pass the probe |
| Three repositories must move together | Contracts, Office and Headquarters each need a released pin | Additive contract plus the inventory flag, so old and new versions can coexist during rollout (V14) |
| Long-lived environments block Office updates | Successive seven-day assignments could prevent a drain from finishing | Fence new starts and successor leases on a draining Office. Confirm stop, preserve authorized disks and reconcile interrupted commands before resuming |
| Image maintenance becomes a company commitment | Each prebuilt image per backend needs patching and re-certification | Start with one image (T3). Publish a patch cadence before adding more |
| A single-machine user must install an Office to get compute | Extra step compared with today's automatic setup | First-run setup already installs an Office on the same machine. Compute consent is one more screen in that installer |
| Silent provisioning | With no prompt, a runaway staffing flow could consume the machine | Ceilings enforced in RuntimeHost, quotas in Headquarters, plan caps on the hosted platform |
| `Hyper-V Administrators` controls every VM on a host | Compute shares that host-wide trust with agent runtimes | Already the documented Office posture. Recommend a dedicated machine when other Hyper-V workloads matter |
| Live runs depend on compute during the transition | Game studio testing uses compute daily | Keep the old path behind a development switch until the Phase 4 gate passes (T6) |
