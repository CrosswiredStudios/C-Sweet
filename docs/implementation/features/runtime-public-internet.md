# Runtime public internet and dependency setup

`RuntimePermissions.PublicInternet` (`runtime.internet.public.v1`) is an installation-time execution
permission. The manifest requests it through `requires`, with `modelVisible: false`; normal installation
and upgrade review approves it. `RuntimeInternetGrant.IsGranted` requires both the manifest declaration
and current grant. Brokered `webAccess`, compute networking and dependency-provider bindings are separate.
It grants outbound public TCP/UDP to the agent VM and child processes, with no inbound hosting authority.
Global package selection and definition synchronization preserve reviewed execution permissions; they
cannot enable a previously declined installation permission.
Existing employees can use `RuntimeInternetAccessReview` on their access display. Its
`AgentInstallationService.ReviewRuntimeInternetAsync` endpoint uses `PluginAdministration`, requires the
current package and grant revision, changes only the internet permission, and uses the grant concurrency
token plus the transactional audit/fencing path. It preserves employee/installation identity.

## Implementation map

- `AgentRuntimeManager.TryStartAsync` resolves a certified development guest for
  `software-development-polyglot-v1`, binds its image digest, network mode and grant revision into the signed
  `RuntimeWorkloadSpecification`, and requires guest protocol 1.1. Empty `DevelopmentImages` discovers
  approved, connected v15 or v15-ipv4 Office images; configured entries instead enforce provider/architecture pins.
  Signed `public-internet-ipv4` mode excludes native IPv6 destinations; full mode requires full IPv6 evidence.
- `ExecutionWorkloadOrchestrator.NetworkPlacementAllows` checks advertised support and guest protocol.
  `RuntimeInternetGrant.IsCurrentAsync` rechecks installation, organization, manifest and grant revision at
  admission, broker opening and lease renewal. `CSweetDbContext.CaptureRuntimeInternetRevocationsAsync`
  serializes grant changes with admission using runtime-row locks, fences assignments, stops runtimes and
  saves audit/realtime outboxes in the same transaction. Notifications are wake hints; reads remain authoritative.
- `OfficeInternetTunnel` handles authenticated egress frames locally in Office Node. `RuntimeInternetEgress`
  opens bounded connected sockets after `PublicInternetPolicy` validates actual destination IPs. It rejects
  private, metadata, special-purpose, host-interface and configured blocked addresses, including mapped IPs
  and mixed public/private DNS answers. Two-frame credit windows bound transfers without dropping downloads.
  The relay closes sockets with its assignment/control/tunnel lifetime.
- `GuestInternetProxy` connects the guest-local TUN adapter and SOCKS5 bridge to that channel. All providers
  retain absent hypervisor network adapters. Restricted runtimes do not create TUN routes. Broker traffic
  continues to Headquarters; TLS and repository credential handling remain unchanged.
- `GuestWorkloadSupervisor` supplies workload tool/home/cache locations. Both developer harnesses permit
  project restores, missing lockfile generation and portable tool setup, while retaining administrator/host
  restrictions. `DevelopmentDiagnostics.FailureKind` distinguishes infrastructure from application failures.
  `PlatformGitWorkspaceClient` omits generated `node_modules` and `.venv` directories while retaining lockfiles.

## Development payload and certification

Office's `build/development-tools.lock.json` pins Node 22.23.3/npm 10.9.9, .NET SDK 10.0.401 and tun2socks
2.7.0 artifacts for x64/arm64. `Prepare-CSweetDevelopmentPayload.ps1` and `provision-development.sh` verify
checksums before extraction. Distribution tools use the dated Ubuntu snapshot and record installed package
versions; the certified guest digest binds the resulting filesystem.

From an elevated Windows PowerShell prompt, prepare a separate development payload without replacing
the installed Office:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ..\CSweet.Office\scripts\windows\Initialize-CSweetWindowsIsolationTest.ps1 -DevelopmentTools -PublicInternetIpv4Only -SkipInstall
```

Firecracker and Apple guests use the Linux rootfs builder with `CSWEET_DEVELOPMENT_PAYLOAD` pointing to
the prepared matching architecture payload. Run the Office smoke runner on the destination host with
`--provider hyperv|firecracker|apple --public-internet true` and the normal helper/image/probe/output/evidence
arguments. It preserves the restricted runtime/builder probes, then tests public TCP/UDP/IPv6, private/host
denial, a missing-lockfile Phaser fixture, portable tools and relay cleanup. Failed checks produce no v15
certification. `ProviderCertificationEvidence.SupportsPublicInternet` requires protocol 1.1 and all required
checks. The user-approved Windows-first limited suite, `csweet-hardware-vm-smoke-v15-ipv4`, excludes only IPv6
reachability from acceptance and preserves its actual failed check. Full v15 still requires IPv6. Inventory
`SupportsPublicInternetIpv6` derives from the stored support bit and full suite identity; older inventory
defaults unavailable. The Offices page displays certified address families. Unit tests are not certification.

## Rollout and live ticket

Packages: SDK 3.66.0, Office Contracts 0.10.0, Office 0.9.0, Software Developer 1.17.1 and Game Engineer 3.5.1.
Apply the additive `AddRuntimePublicInternetInventory` migration with the Headquarters deployment. Deploy
certified Office/image payloads only after normal drain and confirmed zero active assignments, preserving
Office identity. Install/review agent revisions through the ordinary approval flow; deployment alone creates
no installation internet grant. Restricted builder/toolchain workloads retain protocol 1.0 compatibility.

For `VG758236AAD8-13`, verify Node/npm/npx, registry DNS/HTTPS, generated `package-lock.json`, clean `npm ci`
and the ticket's required validation in Daniel's deployed environment before sending the Producer a retry.
Source builds and deterministic dependency fixtures do not establish that this live ticket is recovered.

Verified: 2026-10-13. Local package/unit verification and real Hyper-V certification are recorded below.
Agent rollout remains pending reviewed release publication and installation permission review. The initial rollout targets
Windows/Hyper-V; Firecracker and Apple support stay unavailable until their own hardware certification.

## Current verification evidence

Package consumers were built with `UseLocalCSweetAgentSdk=false`, `UseLocalOfficeContracts=false`,
`UseLocalCSweetWorkManagementContracts=false` and `UseLocalIsolation=false` as applicable. Packed nuspecs
were inspected for SDK 3.66.0, Office Contracts 0.10.0, Software Developer 1.17.1 and Game Engineer 3.5.1.
Both Linux x64 agent bundles passed `pack-csab.py verify`; the Windows Office development bundle passed
destination-host certification and was installed through the normal maintenance upgrade.

| Check | Result |
| --- | --- |
| SDK unit suite | 385 passed |
| Office Contracts suite after flow-control protocol update | 18 passed |
| Office suite, including sustained 1 MiB credit-controlled transfer and IPv4 policy | 225 passed |
| Software Developer / Game Engineer suites | 245 passed each |
| Headquarters broad grant, upgrade, installation and employee checks | 234 passed, 6 skipped |
| Final Headquarters review, inventory reconnect/projection and employee regression checks | 121 passed |
| Isolation Pester suite | 9 passed |

`RuntimeInternetGrantTests` covers offline accepted/declined review audit and wake records, current-grant
checks, cross-organization rejection, revocation fencing and replay. `RuntimeInternetTests` covers
non-public/mapped/host/admin destination blocks, unauthenticated requests, transfer backpressure and the
strict certification gate. Skipped tests supply no runtime evidence.

The real Hyper-V `certification-20261013-141714/windows-hyperv.json` evidence passes the restricted
runtime/builder probes, public IPv4 TLS, DNS UDP, private/host denial, lease cleanup and development fixture.
The fixture discovers Node/npm/npx, generates a Phaser 4.0.0 lockfile, restores with `npm ci`, runs test/build,
and installs portable esbuild into the workload tool prefix. The measured guest is
`sha256:17bec17271622ba012887fa4c0db84774bf03cc1f094ec06259881b0b2fbcf10`; its development-signed Office
0.9.0 payload requires no enrollment identity replacement. IPv6 remains an actual failed check and is
excluded only by the user-approved `v15-ipv4` suite. Firecracker and Apple have no hardware evidence here.
Office 0.9.0 was installed after verified drain to zero assignments, preserving its existing identity.
`OfficeGatewayService.ReplaceProviderInventory` accepts guest protocols 1.0/1.1 and rejects unknown protocols;
`ExecutionFleetEndpoints.Map` preserves the stored internet certification flag in administrator reads.
Headquarters was rebuilt with sibling references disabled; no GitHub/NuGet publication was performed.
The deployed Office reconnected as `71a3fd21-b3ee-4862-99c9-b201d7b1433b`, reports 0.9.0 and
`IPv4 certified; IPv6 unavailable`, and was returned to Ready with new work enabled.
Local deployment proof is `artifacts/public-internet-packages/office-ready-ipv4.png`.
The user will review and push the shared SDK, contracts and agent changes before normal release-driven upgrades.
Agent release publication, explicit installation review, agent-driven validation and Daniel's deployed ticket evidence remain pending.
The scripted fixture does not establish live ticket recovery; no Producer retry has been sent.

## IPv4 guest boot regression (2026-10-13)

Daniel's approved revision-4 runtime used the signed `public-internet-ipv4` policy, but
`ExecutionBrokerSessionRunner.RunRuntimeAsync` set `GuestBootConfiguration.PublicInternet` only for
`public-internet`. The guest therefore remained restricted: no TUN startup and no public resolver,
although Headquarters correctly displayed the signed approved policy. Node/npm were present; npm,
curl and Python failed DNS. Inference records show completed provider calls during this attempt.
The resulting restricted-guest diagnostic was misleading about the administrator's saved approval.

`CreateRuntimeBootConfiguration` now validates the signed policy and enables the guest tunnel for both
approved public modes. Office continues enforcing the address-family limits; absent policy remains
restricted. `RuntimeInternetGrantTests.ApprovedRuntimePolicyEnablesTunnelInGuestBootMessage` checks
the transmitted protobuf for full, IPv4 and absent policies. `RuntimeInternetAccessReview` now changes
its button label after approval and displays a saved-access confirmation. Existing guests require
replacement because network setup is part of boot, not a live toggle.
The local `InternetBootVerified` Headquarters build was deployed after 88 focused tests passed,
using packaged dependencies with sibling references disabled. The old restricted guest was torn down;
Office reconnected with its identity intact. Daniel's permission remains granted at revision 4.
The UI saved-access confirmation was verified by applying the unchanged approved value; no extra
authority or grant revision was created. The ticket's registry and validation evidence still requires
a fresh authoring attempt. No automatic ticket retry or reporting-chain message was sent.

## Network activity audit boundary and future path

`CSweetDbContext.CaptureRuntimeInternetRevocationsAsync` audits grant changes atomically, including offline
accepted and declined reviews. `RuntimeInternetEgress` does not yet persist per-connection activity.
Connection auditing can be added at this mandatory Office socket boundary: record agent/assignment/grant
identity, requested destination, resolved IP, port, transport, decision/reason, times and byte counts.
Hostnames are available only when supplied or observed; do not infer authoritative domains from shared IPs.
Office needs a bounded durable outbox, authenticated Headquarters ingestion with stable receipt IDs, and
the existing protected audit ledger/employee timeline. Define queue-full behavior, retention, access control
and reconnect deduplication explicitly; do not treat best-effort log messages as a complete audit trail.

Headquarters can distribute signed, versioned egress policy while Office enforces it against actual addresses.
TLS content inspection would be a separate reviewed mode with guest trust configuration, credential
redaction, certificate-pinning handling and controls over alternative TCP/UDP paths. It changes this rollout's
end-to-end TLS guarantee and is not implemented or enabled by the internet permission.
