# 04 Verification

[Overview](README.md) · [Work plan](03-work-plan.md) · [Security and risks](05-security-and-risks.md)

Mocked tests prove logic, not a bootable machine. Each row needs a real run, recorded with the
builds used. Record results in the last column when a scenario is run.

| # | Scenario | Deployment shape | Pass when | Result |
| --- | --- | --- | --- | --- |
| V1 | Bootstrap once, reboot, leave the machine at the sign-in screen, then hire the Game Engineer remotely | Private, Windows, same machine | Headquarters and Office start before login. One environment becomes ready with zero post-install elevation launches | |
| V2 | Same, with the Office on a second machine and no Hyper-V on the Headquarters machine | Private, remote Office | Same result | |
| V3 | Owner works only from the phone app | Hosted account | Hire and first build complete without touching the server | |
| V4 | Headquarters on a Linux server without virtualization, Windows Office at the customer | Hosted platform, customer-owned Office | Environment ready on the customer's Office | |
| V5 | Same Headquarters, Linux Office with Firecracker | Hosted platform, hosted Office | Environment ready; limits match the provisioned policy | |
| V6 | Two businesses concurrently request the last slot. Ask for excess CPU, memory and disk separately | All | RuntimeHost accepts at most one last-slot request, refuses excess, and keeps aggregate reservations within the machine ceiling across restart | |
| V7 | Install an Office with compute disabled, then bring an eligible Office online | All | Headquarters skips the disabled Office, the owner sees a capacity notice, and waiting work resumes on the eligible Office | |
| V8 | Command, browser evidence, build archive and published port | Windows and Linux Offices | Results match existing compute. Another tenant cannot open the URL, guest content cannot receive Headquarters cookies, and revocation closes an open stream | |
| V9 | Outbound internet with and without the owner's grant | Windows and Linux Offices | Package installs work with the grant; private and blocked ranges are unreachable; nothing works without it | |
| V10 | Stop and start a persistent environment, then cross the seven-day assignment limit with Headquarters reachable and disconnected | Windows and Linux Offices | Authorized successors see a marker on the same disk with one writer. Expiry offline stops work, closes streams and preserves the disk. An ephemeral environment ends at expiry | |
| V11 | Trigger Office repair and automatic upgrade on an enrolled development machine with the launcher configured. Test an unavailable Maintenance service and an active persistent environment | Windows | Policy survives repair, drain prevents new starts, data survives upgrade, and the app reports an unavailable service. No branch launches UAC | |
| V12 | Restart after VM creation but before acknowledgment, then drop a guest command result after its side effect | All | Redelivery recovers one owned VM and one reservation. The guest command runs once or reports an unknown outcome without automatic re-execution | |
| V13 | Migrate a first-business unsuffixed service and a subsequent suffixed one, with data on a selected volume and a legacy host temporarily offline | Private, Windows | Ownership and drain are verified. Persistent data and shared images still in use survive. Exact-owned legacy services and SYSTEM retirement tasks are confirmed removed when online, and retries leave other businesses intact | |
| V14 | An Office on 0.15.x connected to a new Headquarters | All | Runtimes keep working; compute is reported unavailable, not broken | |

## Automated checks to add

- Contracts: JSON round-trip and signing vector for the new specification; frozen vectors unchanged.
- Office: authorization gate rejects the new kind without compute policy, over a ceiling, and with
  a stale fencing epoch.
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
