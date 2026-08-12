# Distributed execution fleet operations

Agent builds and runtimes execute only through approved `CSweet.ExecutionNode` daemons. The API host
does not execute agent workloads directly.

## Publish the remote-node handoff

Configure the API with the externally reachable HTTPS gateway address and signed package locations:

```json
{
  "CSweet": {
    "ExecutionGateway": {
      "PublicUrl": "https://execution.example.com"
    },
    "ExecutionFleet": {
      "PublicLaunchEnabled": true,
      "AllowUnpinnedDevelopmentImages": false,
      "MinimumBuilderCpuCount": 1,
      "MinimumBuilderMemoryMb": 4096,
      "MinimumBuilderDiskMb": 3072,
      "MinimumRuntimeCpuCount": 1,
      "MinimumRuntimeMemoryMb": 512,
      "MinimumRuntimeDiskMb": 1024,
      "WindowsPackageUrl": "https://downloads.example.com/csweet-execution-node-1.0.0-x64.msi",
      "LinuxPackageUrl": "https://downloads.example.com/csweet-execution-node/linux/",
      "MacOsPackageUrl": "https://downloads.example.com/csweet-execution-macos.pkg"
    }
  }
}
```

`PublicLaunchEnabled` must remain disabled until the released Windows, Linux, and macOS packages have
passed builder and runtime certification. Package URLs are public download locations and must never
contain enrollment credentials.

Production configuration must also pin `CSweet:AgentRuntime:BuilderGuestImageDigest` and
`CSweet:AgentRuntime:RuntimeGuestImageDigest` to the exact certified SHA-256 variants and set
`RequiredCertificationSuiteVersion` to the released certification suite. The API, WorkerHost, and
ExecutionGateway must receive the same policy. `AllowUnpinnedDevelopmentImages` exists only for an
AppHost-launched development fleet and must remain false in packaged or enterprise deployments.

## Add another machine

1. During first-run setup choose **Another machine**, or later open **Settings > Execution Fleet**.
2. Select the target operating system and download its signed package.
3. Copy the automatically created connection code. It is shown once and expires after 15 minutes.
4. Install the package with Windows Installer, APT/DNF, or macOS Installer, then copy the generated
   non-secret configuration command to the target machine.
5. Run the command as administrator or root. Enter the token only through its hidden prompt or
   standard input.
6. Review the compact machine summary in onboarding. Expand the identity details when the full
   certificate fingerprint is needed, then approve or reject the Agent Host.
7. Use **Settings > Execution Fleet** for advanced capacity, provider, image, and certification details.

The page polls for changes automatically. An unused token can be revoked and replaced. Once the node
has its operational certificate, connects over mTLS, and reports qualifying builder/runtime capacity,
its state changes to **Ready**.

## Install on the application machine

The first-run **This machine** path uses the same ExecutionNode and RuntimeHost packages as a remote
machine. Choose **Install Agent Host** to create a protected enrollment and start the
platform-specific privileged installer:

- Windows opens the signed PowerShell installer through the UAC consent prompt.
- Linux opens the packaged systemd installer through PolicyKit (`pkexec`).
- macOS opens the signed launchd installer through the system administrator prompt.

The enrollment credential is written to a temporary user-only file and is never added to a process
argument, URL, or log. The elevated installer consumes and deletes that file. A separate non-secret
result marker and durable setup progress record allow the wizard to recover after browser closure or
an application restart. The wizard automatically approves only a qualifying daemon whose machine and
operating system match the application host. It continues polling until that daemon has an
authenticated connection, a fresh heartbeat, and provider inventory that satisfies the fleet
readiness rule.

Packaged Linux and macOS API distributions include their certified payload under `linux-runtime` or
`macos-runtime`. Source-development builds may instead set `CSWEET_LINUX_EXECUTION_INSTALLER` or
`CSWEET_MACOS_EXECUTION_INSTALLER` to the absolute path of an `install-execution-node.sh` inside a
complete generated payload. These variables identify installers only and must never contain an
enrollment token.

## Manage placement policies

Use **Settings > Execution Fleet > Pools** to create and update typed placement policies. Each pool can
define:

- A hard maximum number of active workloads.
- Required node labels expressed as `key=value` pairs.
- An optional business allowlist. An empty allowlist permits every business.
- Whether it is the global build default, runtime default, or both.

Node labels are edited from the Nodes section. Labels and business allowlists are hard scheduler
filters: work remains queued when no eligible node matches. Default pools cannot be disabled or
deleted, and a pool with active work cannot be disabled.

The Runtime pool overrides section can assign an individual active agent installation to an enabled
pool. Clearing the override returns the installation to the global runtime default. An override is
rejected when the selected pool excludes the installation's business.

Build and runtime defaults may point to different pools. Fleet readiness evaluates each default
independently: the build pool must contain eligible builder capacity and the runtime pool must contain
eligible runtime capacity. Changing one default never creates another implicit pool.

## Upgrade or uninstall a node

Before replacing or removing runtime binaries, select **Drain** for the node in fleet administration.
The gateway sends the drain state down the existing control stream, the daemon persists it locally,
and the scheduler stops assigning new work. Wait until the node has no active assignments. Installers
and uninstallers refuse to stop RuntimeHost unless both conditions are true.

Run the newer platform installer to upgrade in place. It preserves the node identity, operational
certificate, RuntimeHost authentication key, and caches, then restarts RuntimeHost and ExecutionNode.
After the upgraded node reconnects and reports certified inventory, select **Resume**.

For removal, revoke the node in fleet administration and run the installed uninstaller:

- Windows: `& "$env:ProgramFiles\CSweet\ExecutionNodeInstaller\Uninstall-CSweetExecutionFleet.ps1"`
- Linux: `sudo /usr/sbin/csweet-uninstall-execution-node`
- macOS: `sudo /usr/local/sbin/csweet-uninstall-execution-node`

The uninstallers stop and unregister both services and remove their protected runtime state, artifact
caches, helper payloads, and service identities. Their force option is reserved for an already-revoked
node when terminating remaining local work is explicitly intended.

Windows Installer runs the drain-aware fleet uninstaller before removing a configured MSI; an unsafe
removal is rejected. Major upgrades leave the running node intact so it can be drained before the new
staged payload is deployed. The macOS uninstaller also removes the package staging payload, installed
commands, and package receipt. Linux DEB/RPM removal invokes the same drain-aware lifecycle on final
package removal.

## Installed enrollment entry points

- Windows: `& "$env:ProgramFiles\CSweet\ExecutionNodeInstaller\Install-CSweetExecutionFleet.ps1" -PayloadRoot "$env:ProgramFiles\CSweet\ExecutionNodeInstaller\payload" -ControlPlaneUrl 'https://execution.example.com'`
- Linux: `sudo /usr/sbin/csweet-configure-execution-node 'https://execution.example.com'`
- macOS: `sudo /usr/local/sbin/csweet-configure-execution-node 'https://execution.example.com'`

Each native package installs a signed, non-secret staging payload and the fixed configuration and
uninstall entry points. Installing a package alone does not create or start either service. The
configuration command prompts for the one-use token without echoing it, validates the payload, and
then installs the unprivileged ExecutionNode daemon and local privileged RuntimeHost. ExecutionNode
connects outbound to the gateway; RuntimeHost is available only through its protected local pipe or
socket. Enrollment tokens are never MSI properties, package arguments, URLs, or package-manager logs.

Artifact download grants are independently one-use. The gateway records a short database-backed
transfer lease bound to node, assignment, fencing epoch, artifact digest, bearer-token hash, and
transfer identity. Concurrent or completed replays are rejected across gateway replicas. An
interrupted stream can retry with its original transfer identity; successful completion rotates the
stored token hash and permanently consumes that grant.

## Build signed native packages

Build the certified provider payload first. Native package builders reject symbolic links and missing
runtime entry points; their outputs must be published only after the platform's builder/runtime
certification succeeds.

On Windows, install WiX Toolset v4 and the Windows SDK signing tools, then create and Authenticode-sign
the machine-wide MSI:

```powershell
.\scripts\windows\New-CSweetExecutionNodeMsi.ps1 `
  -PayloadRoot .\artifacts\windows-runtime\payload `
  -OutputPath .\artifacts\packages\csweet-execution-node-1.0.0-x64.msi `
  -Version 1.0.0 `
  -CertificateThumbprint 'PUBLISHER_CERTIFICATE_SHA1'
```

On Linux, build signed DEB and RPM outputs on a host matching the payload architecture. The DEB key is
used by `dpkg-sig`; the RPM key is used by `rpmsign`:

```bash
bash ./scripts/linux/new-native-packages.sh ./artifacts/linux-runtime ./artifacts/packages 1.0.0 \
  --format all --deb-signing-key RELEASE_KEY_ID --rpm-signing-key RELEASE_KEY_ID
```

On macOS, use a `Developer ID Installer` identity and a configured notarytool keychain profile. The
builder signs the product archive, submits it for notarization, staples the ticket, and verifies the
final Gatekeeper assessment:

```bash
bash ./scripts/macos/new-installer-package.sh ./artifacts/macos-runtime \
  ./artifacts/packages/csweet-execution-node-1.0.0.pkg 1.0.0 \
  'Developer ID Installer: Example Corp (TEAMID)' CSWEET_NOTARY_PROFILE
```

Linux and macOS packages also contain `runtime-manifest.json`. The manifest binds the exact provider
ID/version/platform/architecture to the helper, signed guest image, detached signature, pinned signing
certificate, and certification evidence. Every referenced path must be relative, declared once, free
of symbolic links, and protected by a lowercase SHA-256 digest. RuntimeHost verifies the entire file
list at startup, derives provider settings only from the validated manifest, and rechecks the helper
digest before every lifecycle or guest-channel operation. A missing, altered, cross-platform, or
path-traversing payload therefore advertises no execution capacity.

## Native helper guest-channel contract

Firecracker and Apple Virtualization helpers expose the certified `stdio-duplex-v1` transport. For an
`open-guest-channel` operation, RuntimeHost starts the configured absolute helper path without a shell
and sends exactly one JSON request line containing the provider-bound workload handle. The helper must:

1. Validate the instance identifier against its own protected workload registry.
2. Open the workload's local vsock or virtio-socket broker endpoint.
3. Write one JSON response line no larger than 4 KiB. A successful probe and channel response advertises
   `guestChannelTransport: "stdio-duplex-v1"`.
4. After the newline, treat stdin and stdout only as opaque broker bytes until either side closes.

RuntimeHost rejects wrong-provider handles, control characters, oversized handshakes, ambiguous CR/LF
framing, missing helpers, timeouts, and helpers that do not advertise the transport. Helper stderr is
drained but never forwarded to a guest or execution node. Closing the channel terminates the helper
process tree.

## Firecracker helper delivery

The Linux package includes the fixed `CSweet.AgentRuntime.Firecracker.Helper` entry point plus pinned
`firecracker`, `jailer`, and `vmlinux` files under `firecracker/`. The installer rejects hosts without
cgroup v2 or read/write KVM access, creates a dedicated unprivileged microVM identity, and grants the
privileged RuntimeHost service only the device, cgroup, filesystem, and Unix-socket access required by
jailer. RuntimeHost itself has no Internet address family.

The helper accepts only the versioned typed operations. It launches each VMM through jailer with a new
PID namespace, cgroup v2 CPU/memory/process limits, a bounded file-descriptor limit, immutable root and
artifact devices, bounded disposable scratch storage, and a virtio-vsock device. It never configures a
network interface. Protected metadata binds every opaque instance ID to its workload ID and kind;
destroy and reaping resolve only through that metadata. Host-initiated broker connections use
Firecracker's `CONNECT <port>` Unix-socket handshake before the helper switches to opaque broker bytes.

Release engineering creates the Linux package with `scripts/linux/new-runtime-payload.sh`. The builder
publishes single-file RuntimeHost, ExecutionNode, and helper executables; accepts pinned upstream
Firecracker, jailer, kernel, initrd, guest-signature, certificate, and certification inputs; and emits the
complete SHA-256 payload manifest consumed by RuntimeHost. It refuses non-Linux runtime identifiers
and non-empty output directories.

### Turnkey Linux development certification

Run the complete development workflow on a native x86-64 or arm64 Ubuntu 24.04 systemd host with
cgroup v2, hardware virtualization, and read/write `/dev/kvm` access. Install the image-building
prerequisites first:

```bash
sudo apt-get update
sudo apt-get install -y curl debootstrap e2fsprogs jq openssl
```

To build and certify without installing services:

```bash
sudo ./scripts/linux/initialize-firecracker-test.sh --skip-install
```

To install and enroll the tested node, create a one-use enrollment in **Settings > Execution Fleet**,
then run:

```bash
sudo ./scripts/linux/initialize-firecracker-test.sh \
  --control-plane https://execution.example.com
```

The installer requests the enrollment token through a hidden terminal prompt or stdin; the token is
never placed in the command line or a URL. The workflow downloads the pinned Firecracker release and
its published SHA-256 companion, constructs a minimal immutable Ubuntu ext4 root filesystem from the
local architecture, copies the local .NET 10 SDK for builder workloads, and emits a matching kernel and
initrd. It then runs both the unprivileged runtime isolation probe and a broker-only real agent build in
separate jailed, no-network Firecracker microVMs. Development evidence and signing credentials are
created only after every check passes. The private development signing key remains in the timestamped
test output and must not be distributed or used for production releases.

The guest uses `/dev/vda` as its read-only root, `/dev/vdb` as bounded disposable scratch, and
`/dev/vdc` as the optional read-only artifact ISO. It validates those fixed roles before starting the
broker. The provider payload also pins the initrd, and Firecracker serial/provider logs are available
through the bounded RuntimeHost log operation for failed-boot diagnostics.

## Apple Virtualization helper delivery

The macOS payload contains a signed `CSweet.AgentRuntime.AppleVirtualization.Helper` with the
`com.apple.security.virtualization` entitlement and a pinned Linux kernel under
`apple-virtualization/`. RuntimeHost invokes only its fixed, versioned operations. Creating a workload
starts a dedicated copy of that same signed helper in an internal workload-host mode because
Virtualization.framework virtual machines must remain owned by a live process.

Each workload host reads mode-0600 metadata from a root-only instance directory and accepts commands
only over a root-only Unix socket. Every command includes a random per-instance authentication token.
The configuration uses a generic platform and Linux boot loader, immutable RAW root and artifact
devices, one bounded disposable RAW scratch device, a virtio entropy device, and exactly one virtio
socket device. Its network-device list is explicitly empty. Host broker connections use the configured
guest port through `VZVirtioSocketDevice`; the short-lived front-end helper relays those bytes through
the certified `stdio-duplex-v1` transport.

Release engineering creates the architecture-specific signed payload with
`scripts/macos/new-runtime-payload.sh`. The builder compiles the Swift helper, publishes single-file
RuntimeHost and ExecutionNode executables, applies hardened-runtime signatures, verifies the
virtualization entitlement, and emits the complete SHA-256 runtime manifest. The installer repeats
signature and entitlement checks, provisions the root-owned VM and socket directories, and installs
the RuntimeHost and ExecutionNode launch daemons. A payload must still pass real builder/runtime
certification before `PublicLaunchEnabled` may be enabled.
