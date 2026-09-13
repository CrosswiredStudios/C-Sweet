# Direct developer applications

Daniel 1.4.0 and SDK 3.43.0 add a general chat-to-code-to-Docker workflow. The configured model interprets requests and authors code; the previous fixed Hello World intake is retained only for already queued legacy work.

## Test flow

1. Update Core and install Daniel 1.4.0, reviewing its added manifest declarations.
2. Ask: **Build a Tetris clone and deploy it. Create your own tickets.** Without a ticket preference, Daniel presents a ticket-ownership question.
3. Follow the personal task on Work. It records repository preparation, coding/testing, source transfer, Docker build, health checking, or a specific blocker.
4. When prompted, the business owner opens Compute and grants the instance a local test link. Daniel resumes automatically and returns an application link that opens in a new tab, plus the source commit and expiry.

Source is stored in a deterministic private internal repository. Core checks the current personal-task claim and owner, installation approval, repository policy/quota, team membership and repository permissions. The agent cannot supply a remote, credential or arbitrary repository to the new `source-control.personal-work.prepare.v1` capability. Manager-created assigned work retains its existing workflow.

## Network authority

Compute defaults do not include network grants. Untouched automatic inbound/publish grants from the earlier Hello World MVP are retired; manually edited grants are preserved. The owner-only local-link action persists instance-scoped inbound and publish-port grants and a compute-change wake event in one transaction. The grant permits only port 8080 until the VM lease expires. Broker and provider independently check authority, and browser connections revalidate access.

Outbound, private-network, inbound and public-endpoint authority are separate actions. The current Hyper-V provider supports disconnected VMs with a brokered loopback link; it does not support guest outbound internet or public endpoints. Approving a manifest is not a network grant.

## Preparation and recovery

The shared Linux-image build installs Docker and caches digest-pinned Python/Node base images. Runtime VMs have no network adapter. Image construction is trusted platform setup, not an agent network session. Upgrading the old template waits for confirmed teardown of existing resources, disables admission against the old template, and invokes the automatic installer/UAC. Provider identity and replay journals are preserved, with payload backup/rollback on failure. Existing VMs are not modified to add Docker.

Each workflow stage, command request and publication generation is retained before dispatch. Durable compute events wake only the owning correlated personal task; the SDK claims it and reads current state. A five-minute deadline recovers missed events. Known Docker build/health-check failures return to the coding model for two repair attempts. Unknown command outcomes do not authorize another command.

## MVP limits and verification

One-hour ephemeral Linux instances; 16 MiB source bundles; 30-second guest commands with 8 KiB output; cached `csweet/python:3.12` and `csweet/node:22` bases; HTTP on port 8080. Source is published before deployment. Missing dependencies and incompatible existing images produce blockers. Long builds, guest outbound dependency installation and public deployments require further provider work.

Regression coverage includes chat ticket ownership and restart recovery, personal repository ownership and claim checks, explicit network grants/revocation, automatic template upgrade gating, exact command/publication replay, build repair and links opening in a new tab. Package verification must use SDK 3.43.0 with sibling SDK references disabled. The Docker-ready image and complete live model-to-VM deployment still require hardware acceptance; the earlier live Hello World success does not verify this new path.
