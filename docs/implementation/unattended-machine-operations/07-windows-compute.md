# Windows Office compute

Office 0.17.0 adds an isolated Linux x64 workspace to the installed Hyper-V RuntimeHost. The Game Engineer 4.6.0 requests it when the employee has one authorized project and the Office has capacity. Assigning that project after hiring also wakes the agent. With several projects the agent can request a workspace for a specific assigned ticket. The Software Developer 2.5.0 uses the same behavior.

## Automatic Windows setup

Headquarters authorizes and schedules work. The enrolled Office runs the agent, build and workspace VMs on the Office host. A combined install places both services on one machine.

The combined release builder reads the compute digest and features from the publisher-verified Office MSI and packages a Headquarters-signed catalog. Fresh combined setup approves its specific local Office enrollment after the Office passes the usual provider qualification. It reserves one Linux workspace when the allocation also leaves room for Office workloads. A single active or draft business receives that reservation. On an empty installation, Headquarters reserves a business ID for the first business created by a Headquarters administrator. The Office receives its fixed business ID during installation in both cases. An explicit disabled compute policy takes precedence. Shared deployments can supply the business and limits in the protected deployment profile.

The hosted Windows development bundle includes both the agent image and a compute image with offline Docker applications. During fresh assisted setup, the installer redeems its machine-bound setup receipt and requests a reservation. Headquarters verifies the image against its own catalog or, in development mode, the official current Office release. It checks the enrollment pool and the setup user's business ownership. On an empty installation with an unrestricted pool, the reservation follows the first business that setup user creates. If several existing businesses qualify, the installer reports the ambiguity. An eligible installation reserves 2 CPUs, 2 GiB of RAM, 20 GiB of disk and one slot. The Office claims the remaining allocation for agent and build VMs. Retries reuse that reservation, and subsequent heartbeats preserve the split.

Office 0.17.0 hosted development bundles shipped without the compute image or an update publisher certificate. Repair reuses their original payload. Moving those installations to the new release requires an administrator-approved install or an already-authorized machine deployment service. The Headquarters database owns existing employee requests.

## Explicit deployment settings

1. Include the normal Office agent image and its hardware certification in the Windows release. Supply the signed Linux compute VHDX separately through `CSWEET_COMPUTE_GUEST_IMAGE` and `CSWEET_COMPUTE_GUEST_SIGNATURE`. The image must boot using generation 2 secure boot, fit the authorized disk ceiling and run the compute guest service on Hyper-V socket port 2763. Declare its installed features with `CSWEET_COMPUTE_GUEST_FEATURES`. The standard developer template requires `docker`.
2. Set the machine allocation and an explicit compute reservation in the combined install's protected `%ProgramFiles%\CSweet\Provisioning\office.json`. The following fragment reserves one workspace. The rest of the allocation is available for Office agent and builder workloads. Use the actual Headquarters business GUID.

   ```json
   {
     "allocatableCpuCount": 8,
     "allocatableMemoryMb": 16384,
     "allocatableDiskMb": 81920,
     "maximumConcurrentWorkloads": 4,
     "compute": {
       "enabled": true,
       "organizationId": "<business-guid>",
       "maximumEnvironments": 1,
       "cpuCount": 2,
       "memoryMiB": 2048,
       "diskMiB": 20480
     }
   }
   ```

3. Merge the following values into the protected `CSweet.ExecutionGateway.json` in the same provisioning directory. Use the SHA-256 of the expanded compute VHDX, including the `sha256:` prefix, and its exact feature set. The enrolled Office's execution pool must allow this business.

   ```json
   {
     "CSweet": {
       "OfficeCompute": {
         "ApprovedImages": [
           {
             "OrganizationId": "<business-guid>",
             "ImageDigest": "sha256:<64 lowercase hexadecimal digits>",
             "Features": ["docker"]
           }
         ]
       }
     }
   }
   ```

4. Complete the combined install and Office enrollment. In Headquarters, use the approved Game Engineer release, with its requested compute grants, and a team assigned to one active or approved project with an accessible delivery board. Use the normal agent release and Installed Agents flow.

## What runs after reboot

The Office advertises capacity on its authenticated control stream. Headquarters checks the business, image, certificate, available slots and resource limits. RuntimeHost verifies the signed request against its enrollment, rechecks its installed policy and enforces its own durable reservations. It creates a differencing disk and attaches zero network adapters. Headquarters receives a ready result after a challenge succeeds inside that exact Linux guest. Guest commands can return up to 16 KiB of combined output and report truncation.

A separate signed receipt identifies the exact operation, sequence and payload. RuntimeHost retains unacknowledged results and lease-expiry audit entries across restart. The expiry audit uses a distinct receipt signature and can be forwarded on reconnect. This Windows path currently supports ephemeral guests, direct guest commands and browser commands that can run offline. Build archive transfer, published ports and outbound networking require additional implementation.

## Post-reboot acceptance

Fresh-install verification on November 22, 2026 used Office 0.17.1 source. The 293 Office tests and 120 focused Headquarters tests passed. `AssistedInstallReservesOnceAndClaimsOnlyTheRemainingOfficeCapacity` exercised both business creation orders. `Hired_employee_replays_office_availability_and_only_the_assigned_office_claims_the_vm` exercised the hire and dispatch in memory (one case). `Hired_employee_replays_office_placement_with_relational_capacity_and_scope_filters` exercised the same journey against disposable PostgreSQL (one case). Each accepted one environment and one lease for the assigned Office. Physical guest boot and pre-login execution with this release still need hardware verification.

Use a disposable Hyper-V Windows guest with nested virtualization. Install the combined release, enroll the Office, approve the agent and prepare the project before reboot. Stage the published `CSweet.Compute.TransportProbe.exe` and its dependencies with the accompanying [acceptance script](../../../scripts/tests/Test-InstalledOfficeComputeHire.ps1) in the lab guest.

Run `Test-InstalledOfficeComputeHire.ps1 -Prepare` inside the Windows guest and keep the returned boot time. Reboot that guest. Run the same script from a one-time, noninteractive lab session scheduled before reboot with `-PreviousBootUtc`, the loopback `-Headquarters` origin, a Headquarters owner credential, `-OrganizationId`, `-TeamId`, the Game Engineer catalog `-AgentReference`, a unique `-RunId`, `-GuestProbe` and its build's `-GuestProbeSha256`. Leave the Windows console at the sign-in screen and wait for the result over Hyper-V Data Exchange. PowerShell Direct can itself produce a type-2 logon, so open a management session only after the test has recorded its final audit. The script submits an actual hire, correlates the employee and environment with a running Hyper-V VM, executes a unique file write/read in that guest and checks the Windows Security log again. A passing record contains both environment and VM IDs, zero interactive sign-ins and zero consent processes. [Hardware results](04-verification.md)
