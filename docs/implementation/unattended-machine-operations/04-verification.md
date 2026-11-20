# 04 Verification

[Overview](README.md) · [Work plan](03-work-plan.md) · [Security and risks](05-security-and-risks.md)

Mocked tests prove logic, not a bootable machine. Each row needs a real run, recorded with the
builds used. Record results in the last column when a scenario is run.

### Windows transport and packaging evidence, 2026-11-20

A signed create and a separately signed command ran against a real Hyper-V Linux guest
with zero network adapters. The command appended a unique marker before its acknowledgment
was discarded. A fresh receiver opened the retained journal, returned `outcome-unknown`
and recorded one reservation. An independent signed guest read found the marker exactly
once. The test removed its VM and differencing disk. The image SHA-256 was
`56241f677edf6f71e7bdf811c9cfad1f0e8e73a702b02e9b8d642889b55adf70`.
The receiver was the [transport probe](../../../tests/CSweet.Compute.TransportProbe/Program.cs).
Restarting the installed Office and dropping a create acknowledgment remain open parts of V12.

Windows Installer also extracted the signed Office development MSI and its fourteen external
cabinets with `msiexec /a`. Reassembling the thirteen image parts produced 6,937,378,816 bytes
with SHA-256 `134492eef35949020d61abb289087e40282d40333e9a179cdbc49fc617faadd4`,
matching the installed Office release manifest. The test used a short-lived local signing key.
Administrative extraction does not exercise enrollment, service startup or the interactive UAC count.

The local combined `0.0.2` bundle passed Burn's layout operation with Office `0.16.0`.
On a fresh generation 2 Windows Server 2025 VM, nested Hyper-V and PostgreSQL were
available before interactive sign-in. A silent install applied the Headquarters MSI.
The Office `InitializeCombinedOffice` action then returned MSI error 1722. Burn returned
1603 and rolled Headquarters back. This run did not reach a post-install reboot.
The isolated guest retains the install logs and a pre-install checkpoint.

After rebooting a combined-install VM, run
[`Test-UnattendedWindowsStartup.ps1`](../../../scripts/tests/Test-UnattendedWindowsStartup.ps1)
from its Hyper-V host. Supply the VM ID, a PowerShell Direct guest credential, and the
loopback Headquarters UI address. The probe checks the nine automatic services and their
accounts, waits for HTTP 200 and checks the Windows Security log for interactive logons
since boot. Record the returned boot time with V1b and exercise the hire separately.

The Headquarters `Hired_agent_waits_for_office_capacity_across_restart_without_local_setup`
regression reopens the platform state, queues the same hired installation, and redelivers
the SDK callback. It observes one Office capacity request, one runtime queue entry, an
authorized pending response, and zero local setup records. Dispatch to an eligible
Office remains a separate gate.

| # | Scenario | Deployment shape | Pass when | Result |
| --- | --- | --- | --- | --- |
| V1a | Office team: bootstrap the Windows Office, reboot it to the sign-in screen and send an authorized compute request from an already running Headquarters | Private, separate Windows Office | Office services start and reconnect before login. One environment becomes ready without elevation. Record the Office build and boot evidence for Phase 2 | |
| V1b | Headquarters Windows MSI team: install Headquarters and Office on one machine, reboot it to the sign-in screen, then hire the Game Engineer from another device | Private, Windows, same machine | The MSI-installed Headquarters services and dependencies start under their configured non-administrator identities, Office reconnects, and one environment becomes ready before anyone logs in. Requires the Windows service delivery and Phase 4 | |
| V2 | Hire the Game Engineer from a Headquarters machine with no Hyper-V and place the request on a second machine | Private, remote Office | One environment becomes ready on the selected Office and Headquarters makes no local setup attempt | |
| V3 | Owner works only from the phone app | Hosted account | Hire and first build complete without touching the server | |
| V4 | Headquarters on a Linux server without virtualization, Windows Office at the customer | Hosted platform, customer-owned Office | Environment ready on the customer's Office | |
| V5 | Same Headquarters, Linux Office with Firecracker | Hosted platform, hosted Office | Environment ready; limits match the provisioned policy | |
| V6 | Two businesses concurrently request the last slot. Ask for excess CPU, memory and disk separately | All | RuntimeHost accepts at most one last-slot request, refuses excess, and keeps aggregate reservations within the machine ceiling across restart | |
| V7 | Install an Office with compute disabled, then bring an eligible Office online | All | Headquarters skips the disabled Office, the owner sees a capacity notice, and waiting work resumes on the eligible Office | |
| V8 | Command, browser evidence, build archive and published port | Windows and Linux Offices | Results match existing compute. Another tenant cannot open the URL, guest content cannot receive Headquarters cookies, and revocation closes an open stream | |
| V9 | Outbound internet with and without the owner's grant | Windows and Linux Offices | Package installs work with the grant; private and blocked ranges are unreachable; nothing works without it | |
| V10 | Stop and start a persistent environment, then cross the seven-day assignment limit with Headquarters reachable and disconnected | Windows and Linux Offices | Authorized successors see a marker on the same disk with one writer. Expiry offline stops work, closes streams and preserves the disk. An ephemeral environment ends at expiry | |
| V11a | Headquarters team: request repair and automatic upgrade on an enrolled Windows Office with the development launcher configured. Exercise drain using an existing workload, then make Maintenance unavailable | Windows, existing Office release | The installed Maintenance service claims the targeted handoffs and completes eligible requests. Drain still protects active work. Headquarters reports the unavailable case. Every case records zero elevation launches | |
| V11b | Office and Headquarters teams: repeat upgrade with a live persistent compute environment and exercise repair after compute enrollment | Windows, Office with compute | Drain prevents new starts, the data survives upgrade, repair preserves the installed compute ceiling and no UAC prompt appears | |
| V12 | Restart after VM creation but before acknowledgment, then drop a guest command result after its side effect | All | Redelivery recovers one owned VM and one reservation. The guest command runs once or reports an unknown outcome without automatic re-execution | |
| V13 | Migrate a first-business unsuffixed service and a subsequent suffixed one, with data on a selected volume and a legacy host temporarily offline | Private, Windows | Ownership and drain are verified. Persistent data and shared images still in use survive. Exact-owned legacy services and SYSTEM retirement tasks are confirmed removed when online, and retries leave other businesses intact | |
| V14 | An Office on 0.15.x connected to a new Headquarters | All | Runtimes keep working; compute is reported unavailable, not broken | |

## Automated checks to add

- Phase 0: inject the lost acknowledgment after the guest has recorded a unique marker. Count
  that marker independently of the operation receipt after restart and replay. Preserve evidence
  for T1 even when the caller can only report an unknown outcome.
- Contracts: round-trip and signing vectors for the selected transport, with frozen vectors unchanged.
- Office: the selected transport rejects compute requests without compute policy, over a ceiling,
  and with a stale fencing epoch.
- Add a deterministic `AgentTestHost` scenario tagged `Category=AgentHarness`. Start at the
  public hire/runtime-queue path that reaches `AgentRuntimeManager.EnsureRuntimeQueuedAsync`
  with an approved compute-capable agent and no Office
  capacity. Assert a durable wait with no `ComputeLocalSetup` or process launch. Supply an
  authorized Office, replay the capacity event on a fresh host over retained state, and assert
  one accepted dispatch and the assigned worker's lease.
- Exercise install, upgrade, repair, retirement, retry and legacy protocol-link routing with a
  launch recorder. Permit an elevation request only during explicit first-install bootstrap.
  Supplement this with a source check across Headquarters, Office and shipped scripts. Counting
  occurrences of `runas` cannot distinguish first install from repair through the same helper.
