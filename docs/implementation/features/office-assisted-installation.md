# C-Sweet Office Assisted Installation Plan

Status: Windows assisted installation and stale-installation recovery implemented  
Last updated: 2026-09-20

## Failed setup diagnostics (2026-10-15)

`ExecutionFleetService.ReportLocalSetupResultAsync` and `Map` preserve a same-attempt
Windows failure record written just before the generic `office_setup_failed` callback.
The callback's later database timestamp must not hide the installer error. Earlier recovery
attempts, mismatched session IDs, and revoked/expired/ready sessions cannot be revived by
progress files. `GetActiveLocalSetupSessionAsync` retains failures across browser reloads.
`AgentHostOnboardingStep` displays error details and the setup reference, suppresses progress
and time estimates for terminal states, and ignores rediscovery of an explicitly dismissed
attempt while the user starts again.

The October 15 attempts failed at `certify-runtime` with `dotnet-sdk-present` in Office 0.9.1.
The hosted image installed `/usr/local/bin/dotnet`; the guest probe requires `/usr/bin/dotnet`.
Office 0.9.2 fixes the shared provisioning recipe and checks the absolute launcher. A rebuilt
Office release and destination-host certification are required before claiming live recovery;
resetting Headquarters or retrying the same bundle cannot correct its guest filesystem.

Regression coverage: `ExecutionFleetServiceTests`, `OnboardingWorkspaceRenderingTests`, and
Office's `scripts/tests/test-development-dotnet-path.sh`.
Local verification passed 119 setup/service/rendering regressions and 225 Office tests with
sibling Office.Contracts disabled. The Linux launcher regression fails with the original
recipe and passes with the fix. Office 0.9.2 binary bundle metadata and packaging checks pass;
the corrected hosted image, live installation and running Headquarters deployment remain pending.

## Office 0.9.2 certification failure (2026-10-15)

The installed release passed the earlier .NET SDK check, then failed inside
`RunInternetSmokeAsync` with `The guest sent an unsupported broker message.`
Office's `GuestBrokerSession.ObserveDiagnosticsAsync` emits `runtime.logs`
`StreamChunk` frames while dependency commands run. The copied certification host
did not handle that message type, although the production Headquarters broker does.
Office 0.9.3 adds bounded authenticated diagnostic handling to
`WindowsSmokeTest/CertificationBrokerHost.cs` and surfaces retained guest diagnostics
on internet-certification errors. Required reports, successful exits, network checks
and lease cleanup still determine certification.

The corrected host then exposed a second failure: npm dependency installation timed
out with `EAI_AGAIN`. Direct public TCP, DNS UDP, and private/host denial checks passed.
Offline inspection of the published VHDX found `/etc/resolv.conf` pointing to Ubuntu's
systemd stub instead of `/run/csweet/resolv.conf`. libguestfs restores the original
resolver around provisioning commands. Office 0.9.3 sets and verifies the link after
customization in `scripts/release/build-hosted-hyperv-image.sh`. Its probe also preserves
bounded stdout/stderr on timeout via `CertificationCommand`, so dependency failures name
the actual command and diagnostic cause.

All 237 Office tests passed against published Office.Contracts. Real certification
of a separate DNS-corrected hosted-image copy passed v15-ipv4 at 18:14 Pacific:
`CSweet.Office/artifacts/c093c/windows-hyperv.json`, guest digest
`sha256:48c8cb2be86c4b724c95bdff6b75bd7c28c1cde11099de70a05a06d01fe4736d`.
Runtime isolation, brokered builds, npm install/ci/test/build, portable tooling,
public TCP/UDP, denied private/host destinations and lease cleanup passed.
Publication of 0.9.3 and normal installation certification remain pending.

## Setup Assistant presentation (2026-10-12)

`CSweet.UI/Pages/Setup.razor` hosts the four onboarding steps in the Setup Assistant:
horizontal progress, a contextual sidebar, and a shared form column. `Setup.razor.css`
and the `.setup-assistant` rules in `wwwroot/css/app.css` own the responsive layout;
the sidebar becomes a compact header on mobile. The saved Aurora/Gold appearance also
applies to provider editor and deletion dialogs. Email and communications remain outside
this flow.

`AgentHostOnboardingStep.LocalExperience` retains capacity presets/custom limits,
Windows approval, missing-handoff and stalled-launch recovery, reassignment, destructive
removal confirmation, removal retry, and readiness gating. Once a local session starts,
the location choices become a compact location summary. Failed, expired, and revoked
sessions show an error indicator instead of a loading spinner. `RemoteExperience` retains
platform instructions, code replacement/copy, identity inspection, approve/reject, and
health checks; Linux local setup continues to use the portable instructions.

`OnboardingWorkspaceRenderingTests` exercises the four steps in both themes, Office
recovery/readiness states, and provider connection, deletion, and media-operation forms.
Its `CSWEET_SETUP_PREVIEWS` output can be loaded with the app and scoped styles for
desktop/mobile layout checks without installing or removing an Office. These rendering
checks do not replace an end-to-end Windows installation test.

## Release-first development onboarding (2026-09-19)

`CSweet.AppHost/Program.cs` enables the Windows launcher without requiring a sibling Office
checkout. `CSweet.OfficeRelease.ps1` selects the newest compatible stable tagged development
bundle within the 20 most recent releases. It requires canonical versioned GitHub URLs, size and
SHA-256 agreement, and safe bounded ZIP extraction. Missing/offline releases allow a configured
local checkout fallback; malformed metadata and bad downloads fail with a visible recovery error.

### Multipart release transport (2026-10-16)

`Assert-CSweetOfficeAsset` and `Receive-CSweetOfficeAsset` support both legacy schema 1
single-file releases and schema 2 multipart bundles from Office 0.11.0. Each multipart
archive has at most 16 ordered, canonically named parts of at most 1 GiB, with a 16 GiB
whole-archive limit. The small support archive remains a single download capped at 10 MiB.
Names and URLs bind every part to the selected repository, version and logical archive.
Part sizes are positive integers; all nonfinal parts have the same size and their sum
matches the complete archive. Missing, reordered, duplicate, cross-tag and invalid parts
fail before downloading.

The receiver stages one part at a time, verifies its exact byte count and SHA-256, and
streams it into a temporary archive with a 1 MiB copy buffer. It verifies the complete
archive's size and digest before invoking the existing bounded, traversal-resistant ZIP
extractor. One total download deadline covers all parts. Failure removes this invocation's
temporary part and archive, preserves pre-existing files, and never silently switches to
local source. The existing automatic setup call site and certification flow are unchanged;
no manual joining, additional tool installation or user choice is required.

Deploy Headquarters with this consumer before publishing/using schema 2 Office bundles.
`tests/OfficeRelease.Tests.ps1` covers discovery, metadata rejection, corruption, truncation,
missing/lost parts, swapped bytes, whole-archive checksum mismatch and cleanup under both
PowerShell 7 and Windows PowerShell 5.1. `.github/workflows/office-release-tests.yml` runs
the consumer fixtures on Windows and Linux. Office's actual publisher-to-consumer fixture
is run with `Test-HostedManifest.ps1 -InstallerScript <path-to-CSweet.OfficeRelease.ps1>`;
it verifies the retained guest image digest after extraction. These are transport tests;
they do not establish a new installed-host certification or successful hosted publication.

Verified 2026-10-16: 76 consumer fixture checks passed under PowerShell 7 and Windows
PowerShell 5.1; eight publisher boundary checks, schema validation and the actual
publisher-to-installer fixture passed. Office's independent Release suite passed 241 tests
against the published Contracts package. No live Office installation or release was changed.

### Automatic setup sequence

`Start-CSweetDevelopmentOfficeSetup.ps1` downloads the small support asset before preflight and
the full bundle after handoff redemption. The Office bootstrap's `PrebuiltRoot` path runs real
Hyper-V certification and local development signing without invoking a compiler or building an
image. It does not enter the source-build mutex. Full bundles use a short unique
`b-<16 hex digits>` staging directory under `%ProgramData%\CSweet\Setup` because Hyper-V appends
VM names and GUIDs during certification. The existing source path remains an availability fallback;
setup, reconnect, repair, and upgrade all prefer the newest compatible stable prebuilt bundle.

`ExecutionFleetService.ReadWindowsSetupProgress` verifies that a running progress record still has
its original live owner. A dead owner becomes `office_setup_interrupted` after 30 seconds so an
AppHost restart or terminated elevated launcher cannot leave onboarding at a stale percentage forever.

`ExecutionFleetService.RefreshLocalSetupEnrollmentAsync` lets the machine-bound setup receipt
refresh an unused enrollment's 15-minute claim window immediately before installation. Receipt
authority ends two hours after redemption; claimed/revoked enrollments and failed sessions cannot
be revived. The browser handoff still expires after five minutes and can be redeemed only once.
The API and `OfficeLocalSetupEndpoints.MapOfficeLocalSetupEndpoints` in ExecutionGateway both
map `POST /api/offices/local-sessions/enrollment-ready`; the launcher uses the gateway origin.
This endpoint is internal to the C-Sweet launcher and does not change Office.Contracts.

Compute image preparation is deferred to `Install-ComputeLocalProvider.ps1` for published Office
installs. The source development path retains its eager preparation. Office installation therefore
does not require the sibling Isolation image-build tools when a hosted bundle is available.

Hosted bundles are development artifacts, described by `office-bootstrap.json`. They are separate
from the production signed-installer `office-release.json` design below. Linux x64 bundles use
the portable root installation command in Office's `docs/60-release/07-hosted-development-bundles.md`;
the automated local browser flow remains Windows-only.

Validation: `tests/OfficeRelease.Tests.ps1`, `ExecutionFleetServiceTests`, `OfficeSchemaRepairTests`,
`OfficeLocalSetupEndpointTests`, and `OfficeUpdateTests`. All 27 release checks and 93 selected
Office service/HTTP endpoint tests passed with
sibling Office.Contracts references disabled. On 2026-09-20, the real Windows PowerShell consumer
selected published `v0.6.1` and verified/extracted both Windows assets. The downloaded Windows
bundle passed real Hyper-V runtime and builder certification (`csweet-hardware-vm-smoke-v14`),
local development signing, and payload assembly with a host compiler-invocation guard. The short
staging path was used, and installation was deliberately skipped to preserve the existing Office.

The published Linux `v0.6.1` tarball passed download integrity, executable-mode checks, Firecracker
and jailer version checks, ext4 integrity, and compiler-free payload assembly. Packaging used
explicitly labeled signature/evidence fixtures; Linux KVM boot/certification remains unverified.
Version 0.6.1 fixes the missing Linux apphost execute permissions in 0.6.0.

Fresh-database browser onboarding exercised explicit removal of the existing Office, release
download, certification, development signing, and payload assembly. The run then exposed a missing
ExecutionGateway enrollment-refresh route (HTTP 404), now covered by HTTP endpoint tests and fixed.
A restarted C-Sweet run still needs to verify installation, enrollment, approval, heartbeat, and
readiness together. Clearing the database does not remove Office services or their former identity;
retain the explicit reconnect/remove recovery flow.

## Update dialog completion (2026-09-28)

`LocalOfficeUpgrade.BeginUpdate` suppresses a previous completed session while the user prepares
a new update. `LocalOfficeUpgrade.Accept` clears that suppression when it accepts the new session,
then retains its `ready` response so the dialog displays success and **Resume work**. Keeping the
suppression set discarded every subsequent completion and left the dialog at **Checking the connection**
even after `ExecutionFleetService.GetLocalSetupSessionAsync` persisted `Ready`.

For an already stuck browser, reload the page and use **Allow new work** after confirming the Office's
new version and healthy provider. Reinstallation is unnecessary when the setup session is `Ready` and
the Office is sending fresh heartbeats. `OfficeUpgradeCompletionTests` covers the pending-to-ready
transition, stale fleet heartbeat data, ignoring an old completion, and unrelated Office sessions.

## Summary

Give the two choices on the Agent Execution setup step meaningfully different experiences:

| Choice | Intended machine | Experience |
|---|---|---|
| **This machine** | The machine on which the user is viewing C-Sweet setup | Download the correct signed installer, guide the user through the required operating-system approval, then hand enrollment to the installed Office without requiring a copied command or connection code. |
| **Another machine** | A different Windows, Linux, or macOS machine | Provide portable package links, the Execution Gateway address, a one-use connection code, and platform-specific instructions that can be transferred to that machine. |

Both paths install the independently released **C-Sweet Office** product. C-Sweet must not
restore an embedded daemon, bundle Office binaries into the headquarters deployment, or
couple Office releases to C-Sweet releases.

## Product decisions

1. **This machine means the browser machine.** It does not automatically mean the server, container,
   or VM hosting C-Sweet. The page must say which machine it is targeting.
2. A normal web page may begin a download, but it cannot silently execute an installer or bypass
   Windows UAC, macOS authorization, or Linux privilege elevation. The user must open the downloaded
   package and approve the operating-system prompt.
3. After installation, C-Sweet should launch a locally registered Office configurator through
   a user-initiated deep link. This removes manual command and code entry without weakening the OS
   security boundary.
4. **Another machine** remains the universal fallback and the required path when the browser machine
   cannot support the assisted handoff.
5. Enrollment remains one-use, short-lived, auditable, and administrator-approved. Assisted setup
   changes how the enrollment reaches the installer, not the trust model.
6. The setup page must never imply that a download was installed, a host was connected, or a host was
   healthy until the control plane has observed the corresponding authoritative state.

## Target user experience

### This machine

1. The user selects **This machine**.
2. The page detects the browser operating system and architecture when possible. It shows the detected
   choice and allows correction before download.
3. C-Sweet resolves an immutable package from `office-release.json` and displays its version,
   publisher, platform, architecture, size, and signature/checksum status.
4. The user selects **Download Office**. The browser downloads the signed MSI/EXE, PKG, DEB,
   or RPM package.
5. The page advances to **Open the installer** and explains the expected publisher and OS elevation
   prompt. It must not claim that the installer was launched merely because the download began.
6. The installer installs the Node and RuntimeHost services plus a small local configuration launcher.
   It registers the `csweet-office` URI scheme where the platform supports it.
7. The user returns to C-Sweet and selects **Connect this machine**. C-Sweet creates a fresh, short-lived,
   one-use assisted enrollment and opens the registered local launcher.
8. The launcher confirms the C-Sweet address and certificate fingerprint, obtains any required user
   confirmation for a private development certificate, stores the enrollment input with restricted
   permissions, starts the Node service, and deletes the input after successful enrollment.
9. The setup page polls authoritative fleet state and transitions through:
   **Downloaded** → **Installer opened** → **Host connected** → **Approval required** →
   **Health check** → **Ready**.
10. The user verifies the machine identity and certificate fingerprint and selects **Approve host**.
11. C-Sweet enables **Continue** only after an authenticated heartbeat reports qualifying certified
    builder and runtime capacity.

### Another machine

1. The user selects **Another machine** and chooses the target platform and architecture.
2. The page displays the immutable signed package link and Execution Gateway address.
3. C-Sweet creates and displays a one-use connection code with an explicit expiration countdown.
4. The user installs Office on the other machine and enters the gateway address and code.
5. The setup page reports connection, approval, health, and readiness using the same authoritative
   fleet state as the assisted path.
6. If the code expires, is consumed, or is hidden after a reload, the page offers **Generate a new
   code**, explains that the previous code will stop working, and immediately displays the replacement.

## Browser and deployment boundaries

The assisted path must not assume that C-Sweet's backend can execute software on the browser machine.
This is especially important when C-Sweet runs in Docker, Kubernetes, a remote VM, or a hosted service.

- The browser may download a signed package after a user click.
- The browser may request that the OS open a registered custom URI after a user click.
- The OS may show a confirmation before opening the Office configurator.
- The configurator may request elevation only for narrowly scoped installation/configuration work.
- The C-Sweet API must not expose a general-purpose command execution or installer-launch endpoint.
- Development-only AppHost conveniences must not become production trust assumptions.

If the URI handler is unavailable, blocked by policy, or unsupported, the page falls back to the
**Another machine** instructions even when the target is physically the same computer.

## Assisted enrollment handoff

### URI contract

Use a versioned URI such as:

```text
csweet-office://enroll/v1#handoff=<opaque-value>
```

The exact value must not contain a long-lived credential. Prefer a purpose-specific assisted-install
handoff over embedding the ordinary enrollment token directly in a URL.

### Handoff requirements

- Random, cryptographically strong, single-use value.
- Maximum lifetime of five minutes.
- Bound to one C-Sweet deployment, intended Office enrollment, and expected gateway origin.
- Stored only as a hash by headquarters.
- Exchanged over HTTPS for the existing one-use enrollment material.
- Invalidated immediately after successful exchange, replacement, rejection, or expiration.
- Never placed in query-string server logs, analytics events, exception text, or audit descriptions.
- Redacted from UI telemetry and Office logs.
- Protected against concurrent redemption; exactly one exchange may succeed.
- The configurator displays the gateway hostname and TLS fingerprint before trusting a private
  certificate. Publicly trusted production certificates require no special prompt.

Implementing the handoff will add cross-repository wire behavior. Update
`CSweet.Office.Contracts` with the smallest dependency-light request/response contract,
increment its semantic version, pack and verify it, and update the pinned package version in both
C-Sweet and `CSweet.Office`. Verify both repositories with sibling project references disabled.

## Headquarters implementation

### Setup API and persistence

Add a purpose-specific assisted installation session with:

- Session ID and hashed handoff secret.
- Enrollment ID.
- Created, expires, redeemed, revoked, and completed timestamps.
- Expected gateway origin and optional expected platform/architecture.
- State: `created`, `redeemed`, `connected`, `approved`, `healthy`, `expired`, `revoked`, or `failed`.
- Sanitized failure code suitable for setup recovery; never persist raw secrets.

Add endpoints for:

1. Resolving the current Office release manifest and compatible asset.
2. Creating/replacing an assisted installation session.
3. Redeeming its handoff for enrollment configuration from the local configurator.
4. Reading setup progress using the existing fleet node/enrollment state as the authority.
5. Revoking an abandoned session.

Keep `/api/offices/claim`, certificate issuance, approval, heartbeat, and gRPC control-plane
behavior authoritative. Do not create a second enrollment or health model solely for the UI.

### Package resolution

- Fetch the configured release manifest server-side with bounded size, timeout, and schema validation.
- Require HTTPS and an allow-listed canonical origin unless an administrator configured a private
  mirror.
- Select only an exact supported OS, architecture, and package type.
- Use immutable versioned asset URLs from the manifest, not mutable `latest` content after resolution.
- Verify the manifest's expected size and SHA-256 in the configurator before executing or installing.
- Show a clear unavailable state when no compatible signed package exists.

### Setup UI state

Separate the presentation target from the persisted execution mode:

- `local-browser`: assisted installation on the browser machine.
- `portable`: instructions for another machine or manual fallback.

Do not reuse the legacy embedded execution-node `local` mode. Both choices ultimately enroll an
independent Office through the normal control plane.

The UI must:

- Preserve the selected target during a setup session and browser reload.
- Show a real button and selected state for both choices.
- Use destination-specific wording throughout the instructions.
- Display exact code/handoff expiry and recovery actions.
- Show the approval card for every newly pending Office, including one whose machine name
  matches the C-Sweet server.
- Never automatically approve a host based only on machine-name equality.
- Stop creating new enrollment sessions once a pending or ready host exists for the current setup.
- Display sanitized server and Node failure codes with a next action.

## Office implementation

### Installer

Each signed package installs:

- `CSweet.Office.Node` service.
- `CSweet.Office.RuntimeHost` privileged service.
- A minimal `CSweet.Office.Configurator` launcher.
- Platform registration needed for the versioned custom URI or equivalent handoff.
- Repair and uninstall registrations.

The generic signed package remains identical for every customer. Do not mutate or wrap signed assets
with deployment-specific configuration after release.

### Configurator responsibilities

The configurator is not a general CLI or long-running third service. It performs a bounded setup task:

1. Parse and validate the versioned handoff URI.
2. Ask for confirmation before contacting a new headquarters origin.
3. Establish HTTPS and show/confirm a private certificate fingerprint when necessary.
4. Redeem the assisted handoff.
5. Write the gateway, trust pin, and one-use enrollment material using platform-protected permissions.
6. Start or restart the Node service.
7. Wait for enrollment or a permanent failure with a bounded timeout.
8. Delete enrollment material after success.
9. Return a human-readable success or recovery result and optionally return focus to the C-Sweet page.

The Node and RuntimeHost remain services. The configurator must not host workloads or retain an
enrollment secret.

## Platform behavior

### Windows

- Prefer a signed bootstrapper or MSI with a signed configurator executable.
- Register the URI handler per machine during elevated installation.
- Display the verified C-Sweet publisher in the instructions before download.
- Let Windows show UAC; do not attempt to bypass it.
- Install under `%ProgramFiles%\CSweet\Office` and keep state under
  `%ProgramData%\CSweet\Office`.
- Preserve the existing application-scoped development certificate trust flow.

### macOS

- Use a signed, notarized, and stapled PKG.
- Register a minimal application/helper capable of receiving the URI handoff.
- Let macOS show its normal installation and authorization UI.
- Keep binaries and state in the established Office locations and launchd identifiers.

### Linux

- Provide signed DEB and RPM packages.
- Register the custom URI through a desktop entry when a supported desktop environment is present.
- Fall back to a displayed `sudo` configuration command for headless hosts or policy-restricted
  desktops.
- Never infer that a browser download means the system package was installed.

## Failure and recovery behavior

### Windows stale-installation recovery

Before redeeming the one-use enrollment material or preparing a development guest image, the elevated
Configurator/launcher classifies the local installation as `none`, `clean`, `active`, or `unsafe`.
Headquarters persists the classification and administrator-selected action on the assisted setup
session; the URI never supplies an authoritative recovery mode.

```text
created -> preflight
  none -----------------------------> redeem -> install
  clean/active/unsafe --------------> recovery-required
  recovery-required + reconnect ----> fresh handoff -> preflight -> redeem -> reconnect
  recovery-required + remove -------> fresh handoff -> staged removal -> redeem -> install
```

**Reconnect this Office** is available only for a complete, recognized installation with no active
assignment markers, authorized RuntimeHost workload handles, or Office-owned Hyper-V VMs. Reconnect
keeps immutable signed runtime packages and certified base images, but deletes the old Node identity,
PFX, enrollment/TLS trust, maintenance state, RuntimeHost Headquarters trust/replay/handle state,
shared runtime key, artifact cache/media, and mutable Hyper-V data. It then creates new local secrets
and enrolls a new Office identity through the normal claim, approval, and heartbeat flow. It does not
resurrect a deleted Headquarters identity.

**Remove Office** requires a separate destructive confirmation. The staged removal helper prefers the
registered MSI force-removal path and falls back to the validated development uninstaller. It removes
services, privileges, URI registration, data, caches, and owned Hyper-V resources, reports each
destructive stage, and continues directly into a fresh installation with the capacity
already selected for the session. Removal is retry-safe. Ordinary upgrades do not
use this recovery path: they still require a drained node and preserve the existing identity only when
active assignments have reached zero.

Recovery state, action, receipts, and completion are machine-bound and time-limited. Result reporting
uses sanitized codes (`existing_office_detected`, `existing_office_active`, `reconnect_unsafe`,
`office_removal_failed`, or `office_setup_failed`) and never includes enrollment material. This first
implementation is Windows-only; macOS and Linux parity remains future work.

Every state must give the user an action that can make progress:

| Failure | Recovery |
|---|---|
| Package unavailable | Select another platform, configure a private mirror, or use manual instructions. |
| Download started but installer not opened | Show **Download again** and platform-specific directions for opening the package. |
| URI handler missing | Show **Install first**, **Try opening again**, and the manual setup fallback. |
| Handoff expired or already redeemed | Revoke it and generate a new assisted handoff without reinstalling services. |
| Enrollment code hidden after reload | Generate and immediately display a replacement; explain that the old code is revoked. |
| TLS certificate untrusted | Show hostname, subject, issuer, validity, and SHA-256; require explicit trust for Office only. |
| Node enrollment rejected | Surface the sanitized control-plane error and offer a new enrollment when appropriate. |
| Host pending approval | Always render its machine identity and fingerprint with **Approve host** and **Not my host** actions. |
| Heartbeat unhealthy | Show provider/runtime diagnostics and keep approval distinct from health. |
| App or browser restarted | Rehydrate non-secret session state and continue polling; require replacement for any secret that cannot be redisplayed. |
| Existing clean Windows Office has no Headquarters identity | Offer **Reconnect this Office** with a new identity, certificate, receipt, and trust. |
| Existing Office has active-work evidence or unrecognized paths | Disable reconnect, explain the evidence, and retain separately confirmed **Remove Office**. |

## Implementation phases

### Phase 1 — Correct path semantics

- Introduce an explicit UI target independent of the legacy persisted `remote` mode.
- Make both cards keyboard-accessible and independently selectable.
- Use the standalone installer workflow for both paths.
- Use local/portable wording consistently.
- Keep approval and health state driven by all relevant enrolled nodes, not a remote-only filter.

### Phase 2 — Release asset resolution and download

- Add validated manifest resolution to headquarters.
- Add compatible platform/architecture selection.
- Add **Download Office** and accurate download/open guidance.
- Display version, publisher, checksum, size, and fallback instructions.

### Phase 3 — Configurator and assisted handoff

- Add the cross-repository assisted-handoff contracts and version bump.
- Add headquarters persistence and one-use redemption endpoints.
- Build the minimal Office configurator.
- Register and handle the versioned URI on Windows first.
- Delete protected handoff/enrollment material after successful use.

### Phase 4 — End-to-end setup state

- Connect download, handoff, enrollment, approval, heartbeat, and readiness into one state machine.
- Add precise progress and recovery states.
- Ensure reload/restart recovery never strands the user.
- Add macOS and supported Linux desktop URI handling, retaining manual fallback.

### Phase 5 — Release hardening

- Validate package signing/notarization and manifest integrity in CI.
- Test clean install, repair, upgrade, uninstall, expired handoff, and interrupted setup on clean VMs.
- Add security review for URI parsing, origin binding, secret redaction, replay resistance, and privilege
  boundaries.
- Publish user-facing setup and administrator troubleshooting documentation.

## Test plan

### Headquarters unit and integration tests

- Both cards retain independent selected state and render destination-specific instructions.
- Assisted sessions expire, revoke, replace, and redeem exactly once.
- Concurrent redemption permits one winner.
- Raw handoff and enrollment secrets are absent from persistence, logs, URLs sent to analytics, and
  error responses.
- Manifest validation rejects unsupported schemas, origins, platforms, architectures, sizes, and
  digests.
- Pending local-machine and remote-machine Offices both render approval actions.
- Approval does not imply health; readiness requires a fresh authenticated heartbeat and certified
  provider capacity.
- Browser reload restores progress and offers replacement when a secret cannot be redisplayed.

### Office tests

- URI parser rejects unknown versions, malformed values, oversized input, and unexpected origins.
- Configurator confirms private certificate fingerprints and rejects mismatches.
- Protected enrollment material has correct platform ACLs and is deleted after success.
- Existing services are configured instead of duplicated.
- Permanent enrollment errors return immediately with an actionable message.
- Installer/configurator never logs handoff or enrollment secrets.

### End-to-end platform tests

- From a clean browser and clean machine, download, install, connect, approve, pass health, and continue.
- Repeat after browser reload, C-Sweet restart, and interrupted installer execution.
- Verify signed Windows, macOS, DEB, and RPM packages on clean VMs.
- Verify the manual **Another machine** path remains functional on every supported platform.
- Verify a remote browser cannot cause the C-Sweet server to execute an installer.
- Verify an unavailable, tampered, expired, or replayed handoff fails closed with a recovery action.

## Acceptance criteria

- Selecting **This machine** produces a distinct assisted-install experience.
- One click begins the correct signed package download; the UI truthfully waits for the user to open it.
- After OS-approved installation, one user action launches configuration and transfers enrollment
  without copying a command or code.
- The user can always fall back to portable instructions.
- Setup never gets stuck without a visible recovery action.
- Host identity requires explicit approval and readiness requires a healthy authenticated connection.
- No headquarters component silently installs privileged software or bypasses OS security prompts.
- C-Sweet and Office remain independently built, tagged, and released.
