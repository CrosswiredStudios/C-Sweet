# Software Developer runtime

The C-Sweet Software Developer is driven by durable Kanban assignment events. Assigning a
development brief leaves the card in To Do, creates exact-installation and work-item-scoped
grants, emits `work.item.assigned.v1`, and queues the installation under the existing global,
organization, and installation runtime limits. One installation processes its FIFO inbox
sequentially because `maximumConcurrentJobs` is `1`.

When leased, the agent moves the card to the first In Progress column and asks the Git workspace
broker to create or resume `/workspace/{workItemId}/{assignmentRevision}`. Microsoft Agent
Framework Harness file access and its unattended local shell are rooted at that directory. The
broker—not the model—materializes Git authentication, publishes only the deterministic ticket
branch, and creates a GitHub pull request. The platform rejects completion without a matching
published workspace, at least one successful validation, and a PR URL.

## Runtime configuration

- `CSweet:AgentRuntime:SoftwareDevelopmentPolyglotImage` must be an immutable digest in deployed
  environments. `:local` is accepted for local smoke testing.
- `CSweet:AgentRuntime:SoftwareDevelopmentEgressProxyUrl` names the controlled proxy reachable
  from the isolated runtime network.
- Build the image with `runtime/software-development-polyglot-v1/build.ps1`. Every base image
  argument must use `@sha256:...`; the release must retain `image-record.json` and an SPDX SBOM.

The image contains .NET 10, Node.js 24, Python 3.14, PowerShell, Git/LFS, OpenSSH, Bash, ripgrep,
Corepack, and uv. C-Sweet still applies a read-only root, non-root UID, dropped capabilities,
`no-new-privileges`, tmpfs, CPU/memory/PID/runtime limits, and an installation-scoped volume.

## Repository setup

An owner or manager creates an organization repository connection through
`/api/organizations/{organizationId}/git/repositories`, grants it to the exact developer
installation, and writes credential components through the write-only credential endpoint.

Supported authentication components:

- GitHub App: `github-app-id`, `github-installation-id`, `github-private-key`
- HTTPS: `https-token`
- SSH: `ssh-private-key`, optional `ssh-key-passphrase`
- SSH plus GitHub PR creation: `github-api-token`

Clone URL host, port, and repository path must exactly match the connection. SSH requires approved
SHA256 host-key fingerprints. GitHub App tokens are minted just in time and refreshed between
prepare and publish operations when necessary.

Credential scope and expiration, repository permissions, branch protection, container isolation,
and the egress policy are the primary controls. Harness shell filtering is defense in depth, as
described by the [Microsoft Agent Framework harness documentation](https://learn.microsoft.com/en-us/agent-framework/agents/harness).

## Failure and retention

Authentication, clone, validation, push, and PR failures leave the ticket In Progress with a
sanitized blocker. A failed workspace is retained for 24 hours by default. Successful publication
and completion remove the assignment directory. Reassignment or unassignment before execution
revokes the old work-item grants and marks its undispatched assignment event as superseded.
