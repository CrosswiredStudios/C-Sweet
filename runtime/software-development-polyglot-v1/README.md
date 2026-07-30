# software-development-polyglot-v1

Curated runtime for the C-Sweet Software Developer. It combines .NET 10, Node.js
24, Python 3.14, Git/LFS, OpenSSH, Bash, PowerShell, ripgrep, Corepack, and uv.

The release pipeline must supply digest-pinned base images to `build.ps1`, scan
the result, publish an SBOM and image record, and configure
`AgentRuntimeManager:SoftwareDeveloperPolyglotImage` with the published digest.
Production configuration rejects a mutable image reference. The local `:local`
tag exists only for developer smoke tests.

At runtime C-Sweet supplies the installation volume at `/workspace`, a tmpfs,
resource and PID limits, a read-only root, dropped capabilities,
`no-new-privileges`, and the approved outbound proxy. Repository credentials are
materialized only by the Git workspace broker and are not baked into this image.
