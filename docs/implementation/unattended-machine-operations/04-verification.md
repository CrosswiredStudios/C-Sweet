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
A second physical run dropped the create acknowledgment before the receiver saved the VM
ID. A fresh receiver recovered the same owned VM and disk, then the command replay again
produced exactly one marker. Both restarts were of the probe process. V12 still requires
the installed Office to survive these boundaries.

Office commit `c60af38` adds a signed creation observation to its private Node–RuntimeHost
protocol. The Windows named-pipe test loses a provider response, returns the original handle
and counts one create. A separate test reopens the RuntimeHost journal and checks both
lost-response boundaries. All 290 Office tests passed with the packaged Contracts dependency.
The physical probe above and these tests do not yet exercise a hired agent on that transport.

Office commit `629ec54` also exercises redelivery through a fresh named-pipe client.
Three cases initially failed when the original signed create reached an Office that
had already recorded the attempt. On redelivery, the receiver now returns the
observation response path for a matching live attempt. The client recovers the
original handle, and the backend create count stays at one. The two receiver-restart
cases cover loss on either side of the helper response. All 292 Office tests passed
against packaged Contracts 0.13.0. The restart cases used an in-memory backend.

Windows Installer also extracted the signed Office development MSI and its fourteen external
cabinets with `msiexec /a`. Reassembling the thirteen image parts produced 6,937,378,816 bytes
with SHA-256 `134492eef35949020d61abb289087e40282d40333e9a179cdbc49fc617faadd4`,
matching the installed Office release manifest. The test used a short-lived local signing key.
Administrative extraction does not exercise enrollment, service startup or the interactive UAC count.

The local combined `0.0.2` bundle passed Burn's layout operation with Office `0.16.0`.
On a fresh generation 2 Windows Server 2025 VM, nested Hyper-V and PostgreSQL were
available before interactive sign-in. A silent install applied the Headquarters MSI.
The Office `InitializeCombinedOffice` action then returned MSI error 1722. Burn returned
1603 and rolled Headquarters back. Running the exact signed packages separately identified
the Office volume check. The 64 GiB OS disk had 46 GiB free against the 52 GiB minimum.
The standalone Headquarters install started all six services and served the UI over HTTP.

After restoring the checkpoint and adding a 96 GiB NTFS data disk, the same combined
bundle installed both products with exit code 0. Before reboot, six Headquarters services
and the Office Node and RuntimeHost ran under their own virtual accounts. Office Maintenance
ran as LocalSystem. The demand-start Migrator had exited. The subsequent startup check
timed out waiting for all nine automatic services. After the forced Hyper-V reset, Windows
listed eight services and the UI returned HTTP 200. The Office Maintenance service was
absent from the Service Control Manager. These installs ran through a pre-elevated
deployment channel. Counting interactive UAC prompts requires a separate interactive launch.

For the interactive install, prepare a clean Hyper-V guest with
[`Test-CombinedWindowsElevation.ps1 -Prepare`](../../../scripts/tests/Test-CombinedWindowsElevation.ps1).
From its ordinary Windows desktop, run the same script with `-Bundle`, the expected
`-Sha256` and a new `-WitnessPath`, then respond to Windows consent. From the lab's
administrative deployment session, pass those same arguments with `-Verify`. The check
correlates the installer PID with process-creation audit event 4688, counts consent
processes during installation, and waits for both products' nine services. Reserve
the guest for this test so another administrator's prompts cannot affect the count.

The debug-signed combined installer `0.0.4` (Headquarters `b60a5c97`, Office
`0.16.0` at `192d469`) installed both products with exit code 0 and generated the
installation's nonexportable Office certificate issuer. On a subsequent normal Windows
shutdown and power-on, all nine automatic services ran under the expected accounts and
Headquarters returned HTTP 200. The guest verified that successful logons were audited
and found zero interactive user logons since startup. It reported the result through
Hyper-V Data Exchange at 23:14 UTC, before the host opened a PowerShell Direct session.
This verifies the service-startup part of V1b. A hire receiving a usable compute
environment remains outstanding.

After rebooting a combined-install VM, run
[`Test-UnattendedWindowsStartup.ps1`](../../../scripts/tests/Test-UnattendedWindowsStartup.ps1)
from its Hyper-V host. Supply the VM ID, a PowerShell Direct guest credential, and the
loopback Headquarters UI address. The probe checks the nine automatic services and their
accounts, waits for HTTP 200 and checks the Windows Security log for interactive logons
since boot. Pass `-ColdBoot` to shut Windows down normally, power the VM back on and
verify that the boot time changed. Record the result with V1b and exercise the hire separately.

For V11a, [`Test-InstalledOfficeMaintenance.ps1`](../../../scripts/tests/Test-InstalledOfficeMaintenance.ps1)
uses an authenticated Headquarters owner session to submit a repair for one Office. Supplying
`-TargetVersion` submits an automatic update through the same public API. The check waits
for a successful session and a fresh heartbeat from the original Office ID.
On the combined 0.0.4 installation, Headquarters approved the Office, received its
heartbeat and accepted the owner's repair request. Office 0.16.0 did not claim the
handoff before it expired. Headquarters reported `office_maintenance_unavailable`.
All nine services were running at expiry.

The Windows loopback test `MaintenanceClaimsWithTheRecoveredCertificateOverTls`
reproduced a Schannel credential failure at the claim endpoint. Office commit
`84474b3` imports a temporary named key under the service account. The test then
observed the expected recovered certificate at the claim endpoint. All 291 Office
tests passed with the packaged Contracts dependency.

On a fresh boot of that Windows Server 2025 VM, an authenticated owner request
completed repair session `362d0992-d1df-4938-8e11-0ae4c0527667`. The original
Office `01068017-b8d3-42a1-abb2-da519449193e` reported a fresh heartbeat at
23:45:43 UTC. The guest reported zero interactive logons since boot. This run
used the debug-signed 0.16.1 Maintenance executable from `496a47f` with the
installed 0.16.0 configurator and runtime. The harness restored the original
service executable and confirmed Maintenance was running after the test.

For the upgrade candidate, the Hyper-V runner from Office `3b03ba3` ran the
`csweet-hardware-vm-smoke-v16-ipv4` suite with the 0.16.1 helper and the same
signed guest image. All 25 checks enabled for IPv4 passed. The runner preserved
the host's installed guest socket registration and removed its disposable VMs.
The evidence SHA-256 was `a78cc6dfebf759e01b619c3aaf8a7886d8d3b0a2755cd4845b91f8da002e6cc9`.
The resulting Office 0.16.1 MSI, its fourteen cabinets, and the update manifest
were signed with the local test publisher. Combined bundle 0.0.5 contains this
Office and the same Headquarters MSI used in the 0.0.4 run.

The [update preparation harness](../../../scripts/tests/Test-AutomaticOfficeUpdatePreparation.ps1)
passed five Headquarters checks and seven installed Office helper checks. The Office
checks cover the publisher requirement, refusal of a source checkout, staging on the
selected volume and preparation before drain. The harness substitutes file retrieval
and control-plane responses.

On the next cold boot, the owner requested an automatic update of the enrolled
0.16.0 Office. Headquarters completed session `0e5b6141-dc8b-4f35-8f30-aa65d09fc4e6`.
Office `01068017-b8d3-42a1-abb2-da519449193e` reported version 0.16.1 and a fresh
heartbeat at 00:17:13 UTC on November 21. The selected storage volume was preserved,
Windows recorded zero interactive logons, and the newly installed Maintenance service
was running after the test fixture was removed. As in the repair test, the initial
0.16.0 service used the 0.16.1 credential fix. The disconnected guest used a test
copy of the release downloader to read the local MSI layout. Release selection,
publisher verification, installation and Headquarters communication ran against the
real signed packages and enrolled Office. GitHub delivery and drain with an active
workload need separate runs.

Office commit `70489e0` keeps existing assignment activity markers visible while
Node recovers its identity at startup. After reading its assignment journal,
Node publishes outstanding assignment markers before removing stale ones.
`InterruptedEnrollmentKeepsRetainedAssignmentVisibleToMaintenance` starts a new
Office worker over retained state, interrupts enrollment and checks that the
previously active assignment still blocks maintenance. All 292 Office tests
passed with the packaged Contracts 0.13.0 dependency. The combined 0.0.5 test
installer predates this change.

The Headquarters `Hired_agent_waits_for_office_capacity_across_restart_without_local_setup`
regression reopens the platform state, queues the same hired installation, and redelivers
the SDK callback. It observes one Office capacity request, one runtime queue entry, an
authorized pending response, and zero local setup records. Dispatch to an eligible
Office remains a separate gate.

| # | Scenario | Deployment shape | Pass when | Result |
| --- | --- | --- | --- | --- |
| V1a | Office team: bootstrap the Windows Office, reboot it to the sign-in screen and send an authorized compute request from an already running Headquarters | Private, separate Windows Office | Office services start and reconnect before login. One environment becomes ready without elevation. Record the Office build and boot evidence for Phase 2 | |
| V1b | Headquarters Windows MSI team: install Headquarters and Office on one machine, reboot it to the sign-in screen, then hire the Game Engineer from another device | Private, Windows, same machine | The MSI-installed Headquarters services and dependencies start under their configured non-administrator identities, Office reconnects, and one environment becomes ready before anyone logs in. Requires the Windows service delivery and Phase 4 | Startup passed on Windows Server 2025 with combined 0.0.4. Hire and placement pending. |
| V2 | Hire the Game Engineer from a Headquarters machine with no Hyper-V and place the request on a second machine | Private, remote Office | One environment becomes ready on the selected Office and Headquarters makes no local setup attempt | |
| V3 | Owner works only from the phone app | Hosted account | Hire and first build complete without touching the server | |
| V4 | Headquarters on a Linux server without virtualization, Windows Office at the customer | Hosted platform, customer-owned Office | Environment ready on the customer's Office | |
| V5 | Same Headquarters, Linux Office with Firecracker | Hosted platform, hosted Office | Environment ready; limits match the provisioned policy | |
| V6 | Two businesses concurrently request the last slot. Ask for excess CPU, memory and disk separately | All | RuntimeHost accepts at most one last-slot request, refuses excess, and keeps aggregate reservations within the machine ceiling across restart | |
| V7 | Install an Office with compute disabled, then bring an eligible Office online | All | Headquarters skips the disabled Office, the owner sees a capacity notice, and waiting work resumes on the eligible Office | |
| V8 | Command, browser evidence, build archive and published port | Windows and Linux Offices | Results match existing compute. Another tenant cannot open the URL, guest content cannot receive Headquarters cookies, and revocation closes an open stream | |
| V9 | Outbound internet with and without the owner's grant | Windows and Linux Offices | Package installs work with the grant; private and blocked ranges are unreachable; nothing works without it | |
| V10 | Stop and start a persistent environment, then cross the seven-day assignment limit with Headquarters reachable and disconnected | Windows and Linux Offices | Authorized successors see a marker on the same disk with one writer. Expiry offline stops work, closes streams and preserves the disk. An ephemeral environment ends at expiry | |
| V11a | Headquarters team: request repair and automatic upgrade on an enrolled Windows Office with the development launcher configured. Exercise drain using an existing workload, then make Maintenance unavailable | Windows, existing Office release | The installed Maintenance service claims the targeted handoffs and completes eligible requests. Drain still protects active work. Headquarters reports the unavailable case. Every case records zero elevation launches | Repair and the signed 0.16.0 → 0.16.1 update passed before login with the Maintenance credential fix and local release delivery. The unpatched service produced `office_maintenance_unavailable`. Active-workload drain pending. |
| V11b | Office and Headquarters teams: repeat upgrade with a live persistent compute environment and exercise repair after compute enrollment | Windows, Office with compute | Drain prevents new starts, the data survives upgrade, repair preserves the installed compute ceiling and no UAC prompt appears | |
| V12 | Restart after VM creation but before acknowledgment, then drop a guest command result after its side effect | All | Redelivery recovers one owned VM and one reservation. The guest command runs once or reports an unknown outcome without automatic re-execution | Hyper-V probe passed for VM and guest command. Installed Office recovery pending. |
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
