# Daniel's automatic Linux Hello World flow

Ask Daniel: "Please create a Hello World application and provide a link to its running test instance."

Daniel 1.3.0 retains the request and uses CSweet.Agent.SDK 3.42.0 through `context.Platform.Compute`. C-Sweet selects the workstream and Linux template, prepares the image through the shared Office/compute image service, enrolls a Windows compute service, and creates bounded scoped grants from the installation's approved capabilities. Windows requests administrator approval where necessary. The user does not run scripts or configure IDs, paths, keys, or templates.

The Hello World chat path is deterministic and does not invoke the LLM. A successful chat acknowledgement means the request was retained, not that provisioning has completed. Core accepts the installed agent's `Normal` personal-task priority as the canonical `Medium` priority.

Windows development hosts automatically share a persisted, non-exportable P-256 control-plane signing key in the current user's certificate store when no explicit signing certificate is configured. Explicit certificate configuration takes precedence; production still requires configuration. Preparation failures are recorded, and an interrupted setup that never wrote an installer handoff can recover automatically on restart.

`GetDefaultsAsync` reports Pending, Running, Ready, or Failed. The durable `com.csweet.compute.available.v1` event wakes retained requests, and a five-minute personal-queue deadline recovers missed wakes. Agents re-read authoritative defaults and resource state before acting. Setup does not revive revoked grants.

The MVP provisions an ephemeral Linux/x64 VM with one CPU, 1024 MiB RAM, a 20480 MiB disk, and a one-hour lease. Daniel writes the Python application inside the VM, starts it through a bounded command, checks its HTTP response, and requests a separate publication of guest port 8080. No outbound network or persistent storage is required for the workload.

The returned `http://127.0.0.1:<port>/` URL works on the compute host machine. It is not an internet link. The response includes its expiry. Browser connections recheck current network authority; stop/destroy closes publication. Unknown command outcomes never produce a fabricated working link or an automatic repeated command.

## Business compute status

The focused business navigation includes **Compute** at `/organizations/{organizationId}/compute`. It refreshes every ten seconds and shows Linux setup progress, observed download bytes, failures, and the most recent 100 compute instances with their agent, project, lifecycle state, resource sizes, lease expiry, and last state change. A warning appears after two minutes without reported setup progress; that warning does not assert that the installer has died. Instance update times represent state changes, not heartbeat guarantees.

The read endpoint requires active human membership in the requested business and returns explicit display fields without provider credentials, handoff secrets, command output, or host paths. Switching businesses retains the Compute section and clears the previous business's data. The internal installer reports preparation stages through a separate protected progress file; existing in-flight downloads can be observed directly from refreshed cache-file metadata.

Downloads show percentage, downloaded/total bytes, recent average throughput, and an approximate remaining duration. Total size comes from cached HEAD responses for strictly matched Ubuntu release filenames on `releases.ubuntu.com`. Estimates require observed byte growth and reset on a new attempt, changed image, or restarted download. Thirty seconds without growth suppresses the countdown. Missing or inconsistent total sizes never produce a percentage or ETA. The download estimate excludes subsequent image preparation and service installation. Image preparation is step 2 of 3 and shows the builder's expected 10–30 minute duration, not an invented countdown. The last two minutes of observations are held in bounded process memory; after an API restart the page briefly measures speed again.

Installer process and completion receipts are protected local files bound to the current handoff hash. The recovery worker checks the recorded PID and creation time after an API restart, detects a dead installer or persisted failure, and updates the durable setup state. The page distinguishes a verified running process from recent stage progress. A terminated failure transcript supports recovery of installers predating these receipts; old-attempt receipts cannot fail a new attempt. The shared image module 1.0.2 uses short VM/export names and checks the fully expanded Hyper-V disk path before building.

## Implementation and deployment

- Core migration: `20260913032010_CompleteAutomaticComputeSetup`.
- Application-owned setup: `ComputeDefaultsService`, `ComputeLocalSetupWorker`, and one-use HTTPS setup endpoints on the execution gateway.
- Internal elevated installer: `scripts/Install-ComputeLocalProvider.ps1`; uses the shared Linux image service and protected ProgramData configuration.
- Typed SDK: 3.42.0. Software Developer: 1.3.0. Packages: `artifacts/compute/mvp/packages`.
- The local development installer verifies the component suite and actual image/runtime hashes before enrollment. The image build receipt is not hardware acceptance evidence.

Component verification: SDK suite and generated-template verification passed; Daniel's 25 tests pass with sibling package references disabled; 384 compute tests passed before the final setup regression additions, and 11 focused setup/broker/TLS tests passed afterward. Shared image/cache tests: 10 passed under Windows PowerShell 5.1. Live VM/link acceptance is recorded separately after the run completes.

## Recovery from interrupted VM activation

Hyper-V rewrites workload ACLs while creating the VM. The provider normalizes those ACLs to SYSTEM, Administrators, and the exact owning VM worker. VM-specific access is limited to that environment's workload tree; provider binaries, credentials, images, and the journal retain strict protection.

An off VM observed after provision/start is reported as `Failed` with `activation-incomplete`. Provisioning I/O or post-creation permission failures are recorded with a durable result rather than silently stopping intake. Unknown physical effects are not replayed. A bounded Core cleanup pass requests destruction of failed generation-one ephemeral environments through the normal broker and existing destroy grants. Persistent environments are preserved, and quota remains reserved until physical teardown is confirmed.

The Compute page warns after two minutes without an update during startup. This indicates potentially stalled work, not proof that the provider process has exited. Local provider repair preserves the enrolled image, signing identity and journal; it re-attests the installed runtime only after passing acceptance checks.

Daniel 1.3.0's Hello World path is a predefined SDK workflow and does not invoke an LLM. Compute lifecycle results, the retained personal task, command results, and the final health-checked URL establish its progress.

Live verification (September 12, 2026): the repaired provider automatically destroyed the failed instance `8007e1a2-fb33-4fb7-b22b-90b2eeaa606e` and confirmed disk teardown. Daniel requested a replacement `f71e307a-7a78-4a61-ba7d-bdad61032993`, which reached Ready. Its guest command exited 0 with `Hello World HTTP health check passed`; port publication completed and the browser displayed Hello World. An independent HTTP request returned 200 in 0.88 seconds. The test URL is ephemeral and must not be treated as a permanent deployment.

Core validation: 401 compute tests passed; Debug API/app builds passed. Daniel 1.3.1 fixes the mismatched Running/InProgress queue-status check and requeues waiting tasks through the existing SDK so the normal claim handler persists their disposition. Its 29 tests and self-test passed using published SDK/contracts with sibling references disabled; the 1.3.1 package version was verified. The live test used installed Daniel 1.3.0 and its scheduled recovery deadlines; the 1.3.1 source release still needs the normal agent update process.

At 11:54 PM Pacific, installed Daniel 1.3.0 posted the verified running-instance link in the original conversation. The browser and a separate HTTP request both confirmed the app response. The event-resume fix remains in the prepared Daniel 1.3.1 release.
