# C-Sweet Office Assisted Installation Plan

Status: proposed  
Last updated: 2026-08-12

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
