# C-Sweet Untrusted Agent Isolation Implementation Plan

## 1. Purpose

This document defines the architecture and implementation plan for safely cloning, building, and running untrusted C-Sweet agents from arbitrary Git repositories.

The central security goal is:

> Untrusted repository content must never execute directly on the host. Every repository-controlled operation—including clone, checkout, dependency restore, code generation, compilation, tests, packaging, startup, and runtime—must occur inside a disposable hardware-virtualized guest.

The runtime must assume that every third-party agent is malicious and may successfully obtain root access inside its guest environment. That outcome is acceptable as long as the agent cannot access the host or any external resource except through the authenticated C-Sweet broker.

> **Infrastructure distinction:** Docker remains a required trusted-infrastructure dependency for the current development stack, including Aspire-managed PostgreSQL. Removing Docker as an *agent isolation mechanism* does not remove that application-infrastructure requirement. Docker availability must never be interpreted as permission to execute untrusted code, and a container must never be used as a fallback for the certified hardware-virtualized guest.

---

## 2. Threat Model

### 2.1 Untrusted inputs

Treat all of the following as hostile:

- Git repositories and every file they contain.
- Git hooks, submodules, and Large File Storage content.
- Dockerfiles and container build files.
- Package manager manifests and lock files.
- Package installation scripts.
- Build scripts and compiler plugins.
- Source generators and post-build targets.
- Test runners and test fixtures.
- Compiled binaries and native libraries.
- Agent entrypoints.
- Agent-generated output files.
- Agent network requests.
- Imported snapshots or cached build artifacts.

### 2.2 In-scope attacks

The architecture must contain:

- Malicious build scripts.
- Dependency supply-chain attacks.
- Container escapes into the guest VM.
- Guest root compromise.
- Attempts to scan or reach the host or LAN.
- Attempts to access cloud metadata services.
- Attempts to read host files or credentials.
- Attempts to communicate with unauthorized external endpoints.
- Resource exhaustion.
- Malicious artifact export.
- Attempts to reuse stale or revoked grants.
- Attempts to impersonate another agent instance.

### 2.3 Out-of-scope attacks

The initial implementation does not attempt to protect against:

- A fully compromised host operating system.
- A malicious host administrator.
- Physical attacks against the host.
- Firmware or CPU supply-chain compromise.
- Unknown hypervisor escapes that defeat the selected platform hypervisor.

The host application and operating system must still follow normal security practices, but the primary goal of this design is containment of untrusted agent code.

---

## 3. Security Invariants

These invariants are mandatory and must be enforced in code and automated tests.

### 3.1 Execution invariant

> Untrusted agent code MUST execute only inside a disposable, hardware-virtualized guest with no host filesystem sharing, no general network interface, and no communication channel other than an authenticated, capability-limited C-Sweet broker.

### 3.2 Build invariant

> Every operation influenced by an untrusted repository—including clone, checkout, submodule retrieval, dependency restore, code generation, compilation, testing, packaging, and startup—MUST occur inside an untrusted Builder VM.

### 3.3 No silent downgrade invariant

> If a certified hardware-backed isolation provider is unavailable, C-Sweet MUST NOT execute untrusted code in a process or shared-kernel container as a fallback.

### 3.4 No direct resource access invariant

> Agents MUST NOT receive direct access to host files, host sockets, databases, message brokers, credential stores, Docker daemons, private networks, or reusable credentials.

### 3.5 Broker-only invariant

> All communication leaving the guest MUST pass through the C-Sweet broker and be authorized using short-lived, instance-bound capability grants.

### 3.6 Disposable environment invariant

> A Builder VM MUST never be promoted into a Runtime VM. Runtime VMs MUST start from a clean, signed image and MUST discard writable state after termination unless an explicitly approved artifact export occurs.

---

## 4. High-Level Architecture

```text
Host Machine
│
├── C-Sweet Control Plane
│   ├── Agent Registry
│   ├── Agent Runtime Manager
│   ├── Isolation Provider Selector
│   ├── Capability and Grant Service
│   ├── Credential Broker
│   ├── Network Broker
│   ├── Build Proxy
│   ├── Artifact Import and Validation Service
│   ├── Audit Log
│   └── Privileged Runtime Service
│
└── Hardware-Backed Agent VM
    ├── Signed Minimal Linux Guest
    ├── C-Sweet Guest Service
    ├── Optional Inner OCI Runtime
    ├── Read-Only Agent Artifact
    ├── Ephemeral Writable Scratch Disk
    └── Broker Socket Only
```

The container layer is optional and exists only for packaging and process lifecycle. The virtual machine is the security boundary.

---

## 5. Platform Isolation Providers

C-Sweet must expose one platform-neutral abstraction while using the strongest native hypervisor available on each supported host.

### 5.1 Supported provider matrix

| Host | Preferred provider | Required boundary |
|---|---|---|
| Linux | Firecracker on KVM | Dedicated guest kernel with jailer and seccomp |
| Windows Pro/Enterprise/Education | Native Hyper-V Generation 2 VM | Dedicated guest kernel with no virtual switch |
| macOS | Apple Virtualization.framework | Dedicated Linux VM with no network device |
| Unsupported host or unavailable virtualization | Remote secure runner | Remote Firecracker/KVM execution |

### 5.2 Explicitly non-certified for untrusted agents

The following must not be accepted as the primary boundary for random third-party agents:

- Standard Docker containers.
- Rootless Docker alone.
- Docker Desktop alone.
- WSL2 alone.
- Process isolation.
- Namespace-only isolation.
- A local runtime with software emulation unless explicitly certified later.

These may still be used for trusted built-in agents under a different security profile.

---

## 6. Core Runtime Abstractions

### 6.1 Isolation provider interface

```csharp
public interface IAgentIsolationProvider
{
    string ProviderId { get; }

    IsolationProviderCapabilities Capabilities { get; }

    Task<IsolationProviderProbeResult> ProbeAsync(
        CancellationToken cancellationToken);

    Task<AgentVirtualMachine> CreateBuilderVmAsync(
        BuilderVmRequest request,
        CancellationToken cancellationToken);

    Task<AgentVirtualMachine> CreateRuntimeVmAsync(
        RuntimeVmRequest request,
        CancellationToken cancellationToken);

    Task StopAsync(
        AgentVirtualMachine virtualMachine,
        CancellationToken cancellationToken);

    Task DestroyAsync(
        AgentVirtualMachine virtualMachine,
        CancellationToken cancellationToken);
}
```

### 6.2 Provider capabilities

```csharp
public sealed record IsolationProviderCapabilities(
    IsolationAssurance Assurance,
    bool UsesDedicatedKernel,
    bool SupportsBrokerSocket,
    bool SupportsReadOnlyBaseDisk,
    bool SupportsEphemeralWritableDisk,
    bool SupportsCpuLimits,
    bool SupportsMemoryLimits,
    bool SupportsDiskLimits,
    bool SupportsNoNetworkDevice,
    bool SupportsSecureBoot,
    bool SupportsMeasuredOrVerifiedBoot);
```

### 6.3 Assurance levels

```csharp
public enum IsolationAssurance
{
    None = 0,
    Process = 10,
    SharedKernelContainer = 20,
    HardwareVirtualMachine = 30,
    CertifiedHardwareVirtualMachine = 40,
    RemoteCertifiedHardwareVirtualMachine = 50
}
```

### 6.4 Agent trust classification

```csharp
public enum AgentTrustLevel
{
    BuiltIn,
    PublisherTrusted,
    OrganizationApproved,
    UntrustedRepository,
    UntrustedMarketplace
}
```

Agents classified as `UntrustedRepository` or `UntrustedMarketplace` must require at least `CertifiedHardwareVirtualMachine`.

---

## 7. Privilege Separation

The main C-Sweet web application must not run with hypervisor administration privileges.

Create a separate native service:

```text
CSweet.RuntimeHost
```

Responsibilities:

- Start and stop virtual machines.
- Attach approved disk images.
- Configure CPU and memory limits.
- Configure the broker socket.
- Refuse network adapters for restricted profiles.
- Validate image digests and signatures.
- Report runtime state to the control plane.
- Destroy disks and VM state.

The service must expose a narrow local RPC interface. It must not expose arbitrary command execution or general hypervisor APIs.

Example operations:

```csharp
public interface IRuntimeHostService
{
    Task<CreateVmResponse> CreateVmAsync(
        CreateVmRequest request,
        CancellationToken cancellationToken);

    Task StartVmAsync(
        VmId vmId,
        CancellationToken cancellationToken);

    Task StopVmAsync(
        VmId vmId,
        CancellationToken cancellationToken);

    Task DestroyVmAsync(
        VmId vmId,
        CancellationToken cancellationToken);

    Task<VmStatus> GetStatusAsync(
        VmId vmId,
        CancellationToken cancellationToken);
}
```

The service must reject:

- Arbitrary host paths.
- Arbitrary executable paths.
- Arbitrary command-line arguments.
- Host directory sharing.
- Bridged networking.
- Device passthrough.
- Raw hypervisor configuration supplied by an agent manifest.

---

## 8. Builder VM Workflow

### 8.1 Builder VM lifecycle

```text
Create clean Builder VM
→ establish authenticated broker socket
→ send repository descriptor
→ clone repository inside guest
→ resolve approved dependencies
→ build and test inside guest
→ package immutable artifact
→ export artifact through controlled stream
→ validate artifact on host
→ destroy Builder VM and writable disk
```

### 8.2 Repository descriptor

The host sends only a declarative descriptor:

```json
{
  "repositoryUrl": "https://github.com/example/agent.git",
  "commit": "0123456789abcdef0123456789abcdef01234567",
  "submodules": false,
  "buildProfile": "dotnet-oci-v1",
  "maximumBuildMinutes": 15
}
```

Requirements:

- Resolve branches and tags to immutable commit hashes before beginning the build.
- Store the resolved commit hash in the agent record.
- Do not rely on mutable branches after resolution.
- Do not execute host-side Git hooks.
- Do not clone the repository onto the host.

### 8.3 Dependency access

The Builder VM receives no ordinary network interface.

All downloads must flow through the Build Proxy over the broker socket.

Initially supported destinations may include:

- GitHub source downloads.
- NuGet.
- npm.
- PyPI.
- crates.io.
- Maven Central.
- Approved language-specific registries.

The Build Proxy must enforce:

- HTTPS only.
- Domain and endpoint allowlists.
- IP validation after DNS resolution.
- Revalidation after redirects.
- Blocking of loopback, link-local, multicast, private, carrier-grade NAT, and reserved addresses.
- Blocking of cloud metadata IPs.
- Maximum request and response sizes.
- Timeouts and bandwidth quotas.
- Request count quotas.
- No host cookies or user credentials.
- Complete audit logging.

The Builder VM must contain no business data or reusable secrets. This reduces the value of any exfiltration attempt through an approved registry.

### 8.4 Build profiles

Do not initially permit arbitrary build instructions from agent authors.

Create versioned, reviewed build profiles such as:

```text
dotnet-oci-v1
node-oci-v1
python-oci-v1
rust-oci-v1
java-oci-v1
binary-bundle-v1
```

Each build profile defines:

- Base guest image.
- Toolchain versions.
- Allowed commands.
- Package registry access.
- Output format.
- Expected entrypoint metadata.
- Build time and resource limits.

A future advanced mode may permit custom Dockerfiles or build scripts, but they must still execute entirely inside the Builder VM.

### 8.5 Build outputs

Preferred output formats:

1. OCI image layout exported as a tar stream.
2. C-Sweet Agent Bundle containing a manifest and immutable payload.

The Builder VM must not write directly into a host directory.

---

## 9. Artifact Import and Validation

All exported artifacts are hostile input.

### 9.1 Import pipeline

```text
Length-limited broker stream
→ isolated staging file
→ digest calculation
→ archive structure validation
→ payload validation
→ malware and vulnerability scanning
→ manifest policy validation
→ content-addressed storage
```

### 9.2 Required validations

Validate:

- Maximum total artifact size.
- Maximum uncompressed size.
- Maximum file count.
- Maximum path length.
- Path traversal attempts.
- Absolute paths.
- Symbolic links and hard links.
- Device nodes.
- FIFOs and sockets.
- Setuid and setgid bits.
- File ownership metadata.
- OCI manifest and layer integrity.
- Supported target architecture.
- Supported operating system.
- Entrypoint declaration.
- Declared broker protocol version.
- Requested capabilities.
- Requested resource limits.
- Duplicate or conflicting manifest data.

### 9.3 Content-addressed storage

Store imported artifacts by immutable digest:

```text
sha256:<digest>
```

Mutable names such as `latest`, branch names, or Git tags may be retained only as metadata pointing to an immutable digest.

### 9.4 Signature model

The initial implementation should sign the validated artifact record using a C-Sweet installation key.

Store:

- Repository URL.
- Commit hash.
- Build profile version.
- Builder guest image digest.
- Dependency proxy policy version.
- Artifact digest.
- Scan results.
- Requested capabilities.
- Build timestamp.
- C-Sweet validation signature.

---

## 10. Runtime VM Workflow

### 10.1 Runtime lifecycle

```text
Create clean Runtime VM
→ attach signed read-only guest image
→ attach validated read-only agent artifact
→ attach fresh ephemeral scratch disk
→ establish broker socket
→ authenticate guest service
→ issue short-lived agent-instance grant
→ start agent
→ service broker requests
→ stop agent
→ revoke grant
→ destroy VM and writable state
```

### 10.2 Runtime VM devices

The restricted runtime profile must include only:

- Virtual CPU.
- Virtual memory.
- Read-only base disk.
- Read-only agent artifact disk or verified artifact transport.
- Ephemeral writable scratch disk.
- Console output routed to bounded logs.
- Host/guest broker socket.
- Minimal entropy device if required.

It must not include:

- Ethernet or Wi-Fi devices.
- NAT networking.
- Bridged networking.
- Host-only networking.
- Shared folders.
- Clipboard integration.
- USB passthrough.
- GPU passthrough.
- Host audio or camera devices.
- Host Docker sockets.
- SSH access.
- Host administration channels.

### 10.3 Optional inner container

The Runtime VM may run the agent through containerd or another OCI runtime for packaging and process management.

Assume the agent can escape the inner container. The VM remains the security boundary.

The inner runtime must not:

- Mount host paths.
- Receive privileged mode.
- Receive host device access.
- Receive control of the guest container daemon unless explicitly required.
- Configure external networking.

---

## 11. Broker Architecture

The broker is the only intentional communication path between agent and host.

### 11.1 Platform transports

| Platform | Preferred transport |
|---|---|
| Linux/Firecracker | virtio-vsock |
| Windows/Hyper-V | Hyper-V sockets |
| macOS/Virtualization.framework | virtio socket |
| Remote runner | mutual TLS over a restricted network channel |

The guest must authenticate the C-Sweet host. The host must authenticate the guest instance.

### 11.2 Session identity

Each VM receives:

- Unique VM identifier.
- Unique agent instance identifier.
- Ephemeral public/private key pair.
- Short-lived boot token.
- Expected artifact digest.
- Expected guest image digest.

The host issues the operational grant only after the guest proves possession of the ephemeral key and reports the expected artifact digest.

### 11.3 Broker request envelope

```csharp
public sealed record BrokerRequest(
    Guid RequestId,
    Guid AgentInstanceId,
    string Capability,
    JsonElement Parameters,
    string GrantToken,
    DateTimeOffset CreatedAtUtc);
```

### 11.4 Broker response envelope

```csharp
public sealed record BrokerResponse(
    Guid RequestId,
    BrokerResponseStatus Status,
    JsonElement? Result,
    BrokerError? Error,
    DateTimeOffset CompletedAtUtc);
```

### 11.5 Semantic capabilities

Prefer narrowly defined operations:

```text
messages.publish
messages.subscribe
files.read-approved
files.export-proposed
calendar.events.read
calendar.events.propose-create
email.messages.read-approved
email.messages.propose-send
finance.transactions.read
finance.expenses.propose
http.connections.invoke-operation
secrets.perform-operation
agent.storage.read
agent.storage.write
```

Avoid generic capabilities such as:

```text
open-tcp
open-udp
execute-host-command
read-host-path
write-host-path
forward-arbitrary-http
use-arbitrary-credential
connect-arbitrary-pipe
```

### 11.6 Grant requirements

Every grant must be:

- Bound to one agent instance.
- Bound to one business or tenant.
- Bound to one capability.
- Bound to one resource or connection definition.
- Limited by scope.
- Limited by expiration.
- Limited by request count or rate when applicable.
- Revocable immediately.
- Included in audit logs.

---

## 12. Credential Broker

Agents should not receive reusable credentials.

Preferred flow:

```text
Agent requests an approved operation
→ broker validates grant
→ host credential broker retrieves secret
→ broker performs or signs the external request
→ broker returns filtered result
```

Examples:

- The agent requests `quickbooks.list-transactions` rather than receiving an OAuth refresh token.
- The agent requests `github.create-issue` rather than receiving a personal access token.
- The agent requests `email.send-approved-draft` rather than receiving SMTP credentials.

When a protocol requires a credential to enter the guest, use a short-lived, narrowly scoped credential with the minimum possible permissions and lifetime. Record this as a higher-risk exception.

---

## 13. Network Broker

Runtime VMs have no network device.

External operations must use declarative host-side connection definitions.

Example:

```json
{
  "connectionId": "quickbooks-production",
  "operation": "list-transactions",
  "parameters": {
    "from": "2026-08-01",
    "to": "2026-08-31"
  }
}
```

The agent must not choose arbitrary IP addresses or ports.

For generic HTTP integrations, define pre-approved connection templates:

```json
{
  "connectionId": "vendor-api",
  "baseUri": "https://api.vendor.example",
  "allowedMethods": ["GET", "POST"],
  "allowedPathPatterns": ["/v1/orders/*"],
  "maximumRequestBytes": 1048576,
  "maximumResponseBytes": 10485760
}
```

The host must enforce:

- Scheme.
- Hostname.
- Port.
- Path pattern.
- Method.
- Header policy.
- Request body size.
- Response size.
- Redirect policy.
- Timeout.
- Rate limit.
- Response filtering.

---

## 14. Guest Image Design

Create signed guest images for:

```text
linux/amd64
linux/arm64
```

### 14.1 Guest image properties

- Minimal Linux distribution.
- Read-only root filesystem.
- No SSH server.
- No default users with passwords.
- No interactive login in production mode.
- Minimal kernel modules.
- No unnecessary services.
- C-Sweet Guest Service enabled at boot.
- Broker socket support.
- Structured logging.
- Automatic shutdown on broker disconnect or lease expiration.
- Time synchronization without external network access.
- Measured image digest reported during handshake.

### 14.2 Separate image classes

Maintain at least:

```text
csweet-builder-dotnet
csweet-builder-node
csweet-builder-python
csweet-builder-rust
csweet-runtime-base
```

Builder images may contain toolchains. Runtime images should remain minimal.

### 14.3 Image updates

- Images must be versioned and signed.
- C-Sweet must reject unsigned images.
- Old images may be revoked.
- A security update should invalidate unsafe cached VMs.
- Image digest and version must be included in audit records.

---

## 15. Platform-Specific Requirements

### 15.1 Linux: Firecracker/KVM

Required configuration:

- `/dev/kvm` available.
- Firecracker jailer enabled.
- Restrictive seccomp filters enabled.
- Dedicated unprivileged service account.
- Namespaces and cgroups configured.
- No TAP or virtual network device for restricted runtimes.
- virtio-vsock broker channel.
- Read-only root and artifact disks.
- Ephemeral writable disk.
- CPU, memory, process, disk, and log limits.

Do not permit direct Firecracker API access from the web application.

### 15.2 Windows: Hyper-V

Required configuration:

- Supported Windows edition with Hyper-V.
- Generation 2 Linux VM.
- Secure Boot when supported by the selected guest image.
- No virtual switch attached.
- Hyper-V socket broker channel.
- Read-only base VHDX.
- Per-instance differencing or ephemeral scratch VHDX.
- No Enhanced Session Mode.
- No shared clipboard.
- No drive sharing.
- No PowerShell remoting into the guest.
- VM management isolated in `CSweet.RuntimeHost` Windows service.

The main C-Sweet application must not require Administrator privileges after installation.

### 15.3 macOS: Virtualization.framework

Required configuration:

- Native Apple Virtualization.framework.
- Architecture-matched Linux guest.
- No virtual network device configuration.
- Virtio socket broker channel.
- Read-only base disk.
- Ephemeral writable disk.
- No directory sharing.
- No Rosetta directory sharing in the restricted profile.
- No clipboard or host-device integration.
- Signed helper process for privileged operations if required.

### 15.4 Unsupported local platform

If no certified provider is available:

- Do not run untrusted agents locally.
- Offer a configured remote secure runner.
- Continue allowing the C-Sweet control plane and trusted features to operate.
- Display a precise capability status to the user.

---

## 16. Remote Secure Runner

The remote runner is the universal fallback and must use the same runtime contract.

```text
Local C-Sweet Control Plane
        │
        │ mutual TLS + signed requests
        ▼
Remote C-Sweet Runner
        │
        ▼
Firecracker/KVM Builder and Runtime VMs
```

Requirements:

- Mutual TLS.
- Runner identity pinning or trusted certificate chain.
- Signed VM creation requests.
- Artifact encryption in transit.
- Per-tenant isolation.
- No reusable local user credentials sent to the runner.
- Same capability broker model.
- Same audit identifiers.
- Runner health and version attestation.
- Ability to revoke a runner.

---

## 17. Resource Controls

Every Builder and Runtime VM must have enforced limits.

Minimum controls:

- Virtual CPU count.
- CPU quota.
- Maximum memory.
- Maximum writable disk size.
- Maximum artifact export size.
- Maximum process count inside guest when supported.
- Maximum runtime duration.
- Maximum build duration.
- Maximum broker request count.
- Maximum broker request rate.
- Maximum concurrent requests.
- Maximum log volume.
- Maximum stdout/stderr line length.
- Maximum Build Proxy bandwidth.
- Maximum Build Proxy response size.

Resource violations should terminate the VM and revoke its grants.

---

## 18. Logging and Auditing

Record:

- Repository URL.
- Resolved commit.
- Agent manifest.
- Build profile.
- Builder image digest.
- Runtime image digest.
- Artifact digest.
- Isolation provider.
- Provider version.
- VM identifier.
- Agent instance identifier.
- VM start and stop times.
- Grant issuance and revocation.
- Broker requests and decisions.
- Build Proxy destinations.
- Artifact validation results.
- Resource-limit violations.
- Crash and termination reason.

Do not record secret values or unrestricted response bodies by default.

Use tamper-evident or append-only storage for high-value audit records.

---

## 19. Agent Manifest

Example:

```json
{
  "schemaVersion": "1.0",
  "id": "com.example.accounting-agent",
  "name": "Example Accounting Agent",
  "version": "1.2.0",
  "runtime": {
    "artifactDigest": "sha256:...",
    "platforms": [
      "linux/amd64",
      "linux/arm64"
    ],
    "entrypoint": [
      "/app/agent"
    ],
    "minimumIsolation": "CertifiedHardwareVirtualMachine"
  },
  "resources": {
    "cpuCount": 2,
    "memoryMegabytes": 2048,
    "writableDiskMegabytes": 2048,
    "maximumRuntimeMinutes": 60
  },
  "capabilities": [
    "messages.publish",
    "finance.transactions.read",
    "finance.expenses.propose"
  ]
}
```

The manifest is declarative. It must not contain raw hypervisor arguments, host paths, arbitrary network devices, or privileged device requests.

---

## 20. Runtime State Machine

```text
Discovered
→ RepositoryResolved
→ BuildQueued
→ BuilderVmCreating
→ Building
→ ArtifactExporting
→ ArtifactValidating
→ ArtifactReady
→ RuntimeVmCreating
→ Starting
→ Running
→ Stopping
→ Destroying
→ Stopped
```

Failure states:

```text
BuildFailed
ArtifactRejected
IsolationUnavailable
RuntimeFailed
PolicyDenied
ResourceLimitExceeded
SecurityViolation
```

Every transition must be persisted and idempotent.

---

## 21. Initial Project Structure

Suggested solution layout:

```text
src/
├── CSweet.AgentRuntime.Abstractions/
├── CSweet.AgentRuntime.Core/
├── CSweet.AgentRuntime.Protocol/
├── CSweet.AgentRuntime.Guest/
├── CSweet.AgentRuntime.HostService/
├── CSweet.AgentRuntime.Firecracker/
├── CSweet.AgentRuntime.HyperV/
├── CSweet.AgentRuntime.AppleVirtualization/
├── CSweet.AgentRuntime.Remote/
├── CSweet.AgentBuild/
├── CSweet.AgentArtifacts/
├── CSweet.AgentBroker/
├── CSweet.AgentCapabilities/
├── CSweet.AgentAudit/
└── CSweet.AgentRuntime.Tests/
```

Platform-specific providers should depend only on abstractions and protocol projects, not on the C-Sweet web UI.

---

## 22. Implementation Phases

### Phase 1: Security contracts and abstractions

Deliverables:

- Threat model.
- Security invariants.
- `IAgentIsolationProvider`.
- Runtime state machine.
- Agent manifest schema.
- Capability and grant model.
- Broker request protocol.
- Audit event schema.

Acceptance criteria:

- Untrusted agents cannot be configured below certified VM isolation.
- Provider selection never silently downgrades.
- Agent manifests cannot supply raw host or hypervisor configuration.

### Phase 2: Guest protocol and minimal runtime image

Deliverables:

- C-Sweet Guest Service.
- Broker socket handshake.
- Ephemeral identity exchange.
- Guest health checks.
- Runtime command protocol.
- Read-only base image build pipeline.
- `linux/amd64` and `linux/arm64` runtime images.

Acceptance criteria:

- Guest starts without a network interface.
- Guest authenticates through the broker socket.
- Guest shuts down when its lease expires.
- Guest cannot access host files.

### Phase 3: Linux reference provider

Deliverables:

- Firecracker/KVM provider.
- Jailer configuration.
- Seccomp configuration.
- Disk lifecycle management.
- virtio-vsock transport.
- Resource quotas.
- No-network runtime profile.

Acceptance criteria:

- A malicious test agent can become root in the guest but cannot reach the host or LAN.
- The guest has no network interface.
- Host files are not mounted.
- VM destruction removes writable state.

### Phase 4: Builder VM and artifact pipeline

Deliverables:

- Builder VM lifecycle.
- Repository resolution.
- Initial build profiles.
- Build Proxy.
- Artifact export protocol.
- Artifact validator.
- Content-addressed storage.

Acceptance criteria:

- Repository files never appear on the host outside the validated artifact staging area.
- Malicious package scripts execute only inside the Builder VM.
- Builder VM is always destroyed after completion.
- Runtime VM never reuses Builder VM state.

### Phase 5: Capability and credential broker

Deliverables:

- Short-lived grant service.
- Instance-bound capability tokens.
- Semantic broker operations.
- Credential broker.
- Connection definitions.
- Revocation.
- Auditing.

Acceptance criteria:

- Agents cannot request arbitrary host commands, paths, or sockets.
- Revoked grants fail immediately.
- A token for one instance cannot be replayed by another instance.
- Reusable credentials do not enter the guest in standard flows.

### Phase 6: Windows provider

Deliverables:

- Hyper-V provider.
- Hyper-V socket transport.
- Generation 2 guest configuration.
- VHDX base and ephemeral disks.
- Privileged Windows service integration.

Acceptance criteria:

- No virtual switch is attached.
- No host drive sharing exists.
- Main C-Sweet application runs without Administrator privileges.
- Untrusted workload cannot reach host network resources.

### Phase 7: macOS provider

Deliverables:

- Virtualization.framework provider.
- Virtio socket transport.
- Architecture-matched guest images.
- Read-only and ephemeral disk configuration.
- Signed helper process if required.

Acceptance criteria:

- No network device is configured.
- No directory share is configured.
- No Rosetta directory share is configured for restricted agents.
- Guest communicates only through the broker socket.

### Phase 8: Remote runner

Deliverables:

- Remote provider implementation.
- Mutual TLS.
- Runner registration and revocation.
- Artifact transfer.
- Remote VM lifecycle.
- Health reporting.

Acceptance criteria:

- Unsupported hosts can run untrusted agents remotely without local downgrade.
- Remote runner requests are authenticated and tenant-bound.
- Local grants remain revocable.

### Phase 9: Hardening and certification

Deliverables:

- Platform certification suite.
- Escape-path tests.
- Resource-exhaustion tests.
- Broker fuzzing.
- Artifact parser fuzzing.
- Dependency and image update process.
- Incident response playbook.

Acceptance criteria:

- Each provider passes the same security contract tests.
- Provider version and certification status are visible in the UI.
- Unsafe provider configurations are rejected automatically.

---

## 23. Security Test Suite

Create deliberately malicious fixtures.

### 23.1 Repository tests

- Git hook attempting host command execution.
- Malicious submodule URL.
- npm `postinstall` script.
- NuGet/MSBuild custom target.
- Python setup script.
- Rust build script.
- Makefile writing outside the project directory.
- Dockerfile attempting privileged mounts.

Expected result:

- Code executes only inside Builder VM.
- Host remains unchanged.

### 23.2 Filesystem tests

- Read `/etc/host-secret`.
- Search for host home directories.
- Attempt path traversal during export.
- Create symlink and hard-link archive attacks.
- Create device-node archive entries.
- Attempt to write to read-only artifact disk.

Expected result:

- Host files are unreachable.
- Invalid exports are rejected.

### 23.3 Network tests

- Scan private IP ranges.
- Reach host loopback.
- Reach cloud metadata IP.
- Resolve malicious DNS rebinding hostname.
- Open arbitrary TCP and UDP sockets.
- Attempt DNS exfiltration.

Expected result:

- Runtime VM has no network interface.
- Builder access is limited to Build Proxy policy.

### 23.4 Broker tests

- Reuse expired grant.
- Reuse another instance's grant.
- Change tenant identifier.
- Request undeclared capability.
- Send oversized message.
- Flood requests.
- Request arbitrary URL.
- Request arbitrary host path.

Expected result:

- Request denied and audited.
- Rate and size limits enforced.

### 23.5 Resource tests

- Fork bomb.
- Memory exhaustion.
- Disk exhaustion.
- Infinite loop.
- Excessive log output.
- Oversized artifact export.

Expected result:

- VM is throttled or terminated.
- Host remains responsive.
- Grant is revoked.

### 23.6 Container escape assumption test

Provide a test agent that obtains root inside its inner container or bypasses the inner container runtime in a controlled test environment.

Expected result:

- Agent remains inside the VM.
- No host channel becomes available beyond the broker socket.

---

## 24. Operational Requirements

- Automatically check hypervisor availability during onboarding.
- Show the detected provider and assurance level.
- Refuse untrusted agent execution if certification fails.
- Keep hypervisor, guest images, and broker protocol versioned.
- Support emergency revocation of guest images and provider versions.
- Never recommend disabling host security features to make C-Sweet work.
- Provide clear diagnostics without exposing sensitive host details to agents.
- Preserve audit records after VM deletion.

Example status display:

```text
Untrusted Agent Isolation: Certified
Provider: Hyper-V
Guest Image: csweet-runtime-base 1.4.2
Network: Disabled
Host Filesystem Sharing: Disabled
Broker Transport: Hyper-V Socket
```

---

## 25. Definition of Done

The untrusted agent runtime is complete when:

1. A random Git repository can be selected without cloning it onto the host.
2. The repository is cloned, restored, built, tested, and packaged inside a disposable Builder VM.
3. The resulting artifact is exported through a bounded channel and validated before storage.
4. The artifact runs in a separate clean Runtime VM.
5. The Runtime VM has no general network interface.
6. The Runtime VM has no host filesystem sharing.
7. The only host communication path is an authenticated broker socket.
8. Every broker action requires a short-lived, instance-bound grant.
9. Real credentials remain outside the guest for standard operations.
10. Resource limits can terminate abusive agents without destabilizing the host.
11. Builder and Runtime writable state is destroyed after use.
12. Linux, Windows, and macOS providers pass the same certification suite.
13. Unsupported hosts use a remote certified runner or refuse execution.
14. No code path silently falls back to shared-kernel containers for untrusted agents.

---

## 26. Immediate Next Tasks

1. Create `CSweet.AgentRuntime.Abstractions`.
2. Define the provider, VM request, runtime state, trust-level, and assurance-level contracts.
3. Define the agent manifest JSON Schema.
4. Define the guest/host broker protocol using protobuf or another length-delimited binary protocol.
5. Implement a fake in-memory provider for orchestration tests.
6. Implement the runtime state machine and persistence.
7. Build the first minimal Linux guest image.
8. Implement the Firecracker provider as the reference implementation.
9. Prove a no-network, vsock-only guest can boot, authenticate, execute a sample agent, and be destroyed.
10. Add the first malicious fixture tests before implementing repository build support.

Do not begin with Docker integration. First prove the VM boundary, broker-only communication, provider contract, and lifecycle guarantees. Docker or containerd can be added inside the guest later as a packaging implementation detail.

---

## 27. Mandatory Legacy Removal and Cutover

The VM architecture is a replacement, not an additional optional execution path for untrusted agents. The implementation must remove obsolete agent-container code wherever the VM boundary supersedes it.

Required cutover work:

- Delete the Docker agent runner, Docker build executor, Docker command abstraction, and agent-host Dockerfile.
- Remove Docker volume, bind-mount, private-network, secret-file, image-name, and container-identifier settings from active agent runtime code and UI.
- Replace container-specific persistence with provider-neutral isolation provider and provider-instance identifiers.
- Replace host workspace/package paths with opaque broker or content-addressed locators; never reinterpret those locators as filesystem paths.
- Remove shared-kernel fallback registration and fail closed when RuntimeHost authentication, a certified provider, a signed guest image, or current certification evidence is unavailable.
- Disable existing executable installations during migration, discard legacy runtime/build state, invalidate Docker-produced artifacts, and queue clean Builder VM rebuilds.
- Retain references to containers only where they describe an optional inner guest packaging layer, deliberate negative tests, unrelated trusted infrastructure, or historical migrations.

### 27.1 Repository implementation status

Implemented in the current cutover:

- Provider-neutral isolation contracts, workload specifications, selection policy, state, trust, assurance, certification, artifact, image, and remote-runner contracts.
- Authenticated, replay-resistant, bounded RuntimeHost local RPC and an independently deployable privileged `CSweet.RuntimeHost` service.
- Fail-closed Hyper-V, Firecracker/KVM, and Apple Virtualization provider adapters using a narrow typed native-helper protocol.
- Guest boot identity proof, lease enforcement, channel-loss shutdown, confined process launch, bounded logs, and a guest-local Unix-socket MCP proxy over the authenticated broker channel.
- Semantic broker grants with tenant/workload/channel/image/artifact binding, bounded proxy traffic, and ordered bounded builder-artifact streaming.
- Artifact quarantine, digest and archive validation, content-addressed storage, and installation-key signing.
- Builder VM and Runtime VM orchestration through the provider-neutral abstraction, with unconditional VM destruction on failed or completed builds.
- Destructive database cutover migrations and removal of the production Docker agent execution/build path and host path/image settings.
- Windows first-run isolation onboarding with read-only edition, firmware, SLAT, DEP, memory, Hyper-V feature, hypervisor, restart, RuntimeHost, image, helper, and certification readiness checks.
- Explicit, audited UAC-assisted Hyper-V feature enablement using the fixed Microsoft DISM feature operation with automatic restart disabled.
- A narrow Windows Hyper-V lifecycle helper that creates Generation 2 VMs with no virtual network adapter, Secure Boot, static memory and CPU controls, differencing OS disks, bounded scratch VHDX disks, and rooted instance cleanup.
- RuntimeHost verification of actual guest-image and certification-evidence bytes plus a detached guest-image signature pinned to a configured X.509 signer certificate.
- Windows AF_HYPERV/Linux AF_VSOCK broker transport bound to the exact Hyper-V VM identifier, with boot configuration, challenge-response guest authentication, lease enforcement, and AgentHost-only semantic request forwarding.
- Content-addressed single-file ISO-9660 artifact media attached as a Hyper-V virtual DVD; the guest mounts it read-only with `nosuid,nodev,noexec`, re-verifies SHA-256, rejects links/special files/path traversal, and extracts only into disposable guest storage.
- UAC-assisted RuntimeHost onboarding backed by a manifest-verifying, versioned Windows service installer with explicit key/artifact ACLs, HVSock registration, service-scoped environment, and shared-key file loading.
- A Windows payload builder that publishes self-contained RuntimeHost/helper binaries and packages the signed guest image and provider certification evidence for onboarding.
- Semantic certification-evidence validation bound to the exact provider version, host OS/architecture, guest digest, broker protocol, suite version, and certification window.
- A reproducible Windows developer-image pipeline pinned to Packer's Hyper-V plugin and an official checksum-verified Ubuntu Server ISO, with Secure Boot, first-runtime-boot SSH hardening, the guest broker service, and disposable scratch-disk preparation.
- A one-command UAC-assisted Windows test bootstrap that enables Hyper-V when needed, builds or reuses the VHDX, publishes native test tools, boots a real no-network VM, exercises artifact delivery and the authenticated Hyper-V socket broker, emits evidence only after in-guest isolation checks pass, signs the exact tested image, and installs a development RuntimeHost payload.

Windows developers can now produce, certify, sign, package, install, and test the complete local Hyper-V path without a prebuilt artifact. Production deployment prerequisites remain intentionally fail-closed rather than simulated: release-produced signed guest images, the protected private signing/release pipeline, current evidence from the complete malicious-fixture certification suite, macOS/Linux certification, and (where local certified virtualization is unavailable) a configured remote certified runner. Until the appropriate release artifacts are packaged, installed, and certified, C-Sweet continues to operate its trusted control plane but refuses to execute untrusted agents.
