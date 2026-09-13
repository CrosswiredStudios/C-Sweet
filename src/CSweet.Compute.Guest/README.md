# Compute guest MVP runtime

This executable belongs inside an isolated Linux or Windows Hyper-V guest. Core and the Hyper-V provider do not reference the guest execution assembly. The host isolation boundary is the VM; guest job objects provide ordinary process-tree cleanup, not an additional security sandbox.

Run `CSweet.Compute.Guest.exe --serve-hyperv` inside the guest. The listener binds HV_GUID_PARENT on fixed port 2763, accepts up to eight connections, and permits one active command. There is no TCP, same-partition, or bare-metal fallback. The Hyper-V parent binding is documented by [Microsoft](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service).

The protocol supports existing readiness probes and a single bounded command per connection. Requests and results carry matching challenge/request IDs. Stdout and stderr share the requested byte budget. Commands have explicit deadlines; cancellation/parent exit trigger job cleanup. Native launch uses an explicit executable, encoded literal arguments, an explicit inherited-handle list, and a minimal environment. Captured output is untrusted bytes. A lost response is an unknown outcome; the provider client never automatically repeats the command.

The operator must install this payload only in the isolated guest, with no host filesystem shares, hypervisor/provider credentials, or host Docker socket. The MVP runner uses the guest runtime account. Stronger guest account/token separation, durable guest execution receipts, Windows SCM installation remain deferred or pending integration; none is silently supplied by this executable.

Publish output is available at `artifacts/compute/mvp/guest` and requires .NET 10 inside the Windows guest. Broker command routing and physical VM acceptance are still pending. See `docs/implementation/compute-mvp.md` for the test-release gates.

## Linux (preferred MVP)

Ubuntu 24.04 with systemd 254+ is the first Linux target. The self-contained `artifacts/compute/mvp/linux-guest` payload does not require a separate .NET installation. Copy it and `scripts/Install-ComputeGuestLinux.sh` into the VM, then run the installer as root with the absolute payload directory. It refuses bare-metal/non-Hyper-V environments and existing installations, loads hv_sock, and installs/starts the guest service. Docker is not required.

Linux listens on VSOCK port 2763 and rejects peers other than host CID 2. Commands run as transient systemd services with explicit working directories, no variable expansion, cleared environments, shared output bounds, and control-group termination. A service runtime limit backs up the coordinator deadline. Workload commands run under the guest runtime account; this is not an additional sandbox within the VM. SIGTERM requests graceful cleanup.

Native systemd command execution and Hyper-V VSOCK acceptance require a real guest test. Cross-platform protocol tests and an unprivileged Ubuntu binary smoke check do not substitute for that acceptance.