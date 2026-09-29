# software-development-polyglot-v1

This directory contains the earlier Docker development image and its build
recipe. The current agent launcher uses certified Office guest VMs and does not
launch this image. Building or tagging it does not change an agent's tools.

The manifest environment profile remains `software-development-polyglot-v1`.
`AgentRuntimeManager.TryStartAsync` checks that this profile requests a writable
workspace, then resolves the configured `RuntimeGuestImageId` and digest through
`FleetGuestImageRegistry`. There is no current
`AgentRuntimeManager:SoftwareDeveloperPolyglotImage` option.

Office certifies one guest image per provider configuration. Supplying Node,
npm, browser binaries, or other development tools requires provisioning the
Office guest and rebuilding and recertifying its image, or using an authorized
managed toolchain workflow. A profile name alone does not prove those tools are
installed. The assigned QA harness does not automatically invoke the Node
TypeScript toolchain service.

The retained Docker recipe combines .NET, Node, Python, Git/LFS, OpenSSH, Bash,
PowerShell, ripgrep, Corepack, and uv for development image experiments. Its
`build.ps1` requires digest-pinned base images. It is not an alternative agent
isolation backend.

See [Office guest images](../../../CSweet.Office/docs/30-workloads/06-guest-images.md)
and the [QA diagnostics](../../docs/implementation/debug-guide.md).
