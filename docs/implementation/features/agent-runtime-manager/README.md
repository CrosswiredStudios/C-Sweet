# Agent Runtime Manager

> **Superseded historical design:** This feature plan describes the removed Docker container
> runner. Use the [agent isolation implementation plan](../../../../Documentation/Architecture/CSweet_Untrusted_Agent_Isolation_Implementation_Plan.md)
> and [security architecture overview](../../../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md)
> for the provider-neutral, hardware-virtualized runtime.

## Goal

Add an Agent Runtime Manager that can run both always-on agents and scheduled ephemeral agents.

When complete, a user should be able to:

1. Open the Agents page.
2. Click **Import Agent**.
3. Paste a public GitHub URL for a .NET agent project.
4. Preview the root `csweet-agent.json`.
5. Approve bounded permissions and container limits.
6. Configure a tick frequency.
7. Let C-Sweet clone, build, package, and schedule the agent.
8. Have C-Sweet start the agent in a container on each tick.
9. Let the agent connect to the broker, do its work, report completion, and exit.
10. Have C-Sweet stop and clean up the container until the next tick.

The key design rule is:

> Installed agents are metadata. Running containers are temporary runtime instances.

## Feature docs

1. [Architecture](./01-architecture.md)
2. [Global Configuration](./02-global-configuration.md)
3. [Phased Implementation Plan](./03-phased-implementation-plan.md)
4. [Junior Developer Checklist](./04-junior-developer-checklist.md)

## Relationship to GitHub Agent Import

This feature is the runtime half of GitHub agent import. The import feature handles source,
manifest, approval, and package identity. The runtime manager handles activation, scheduling,
container lifecycle, limits, heartbeats, completion, and cleanup.

Use this plan together with
[GitHub Agent Import](../github-agent-import/README.md).

## Non-goals for the first version

- Kubernetes support.
- Multi-host scheduling.
- Private repository imports.
- Automatic marketplace billing.
- Running arbitrary non-.NET projects.
- Giving imported agents raw database credentials, Docker socket access, secret-store access, or
  any path that bypasses the broker.

## Definition of done

- Always-on agents can be started, stopped, and observed.
- Periodic agents can be scheduled by tick frequency.
- Each tick creates at most one active runtime instance per installation unless configured
  otherwise.
- Ephemeral containers stop after completion, timeout, cancellation, or failure.
- Global agent/container settings are editable from the Configuration page.
- Imported .NET agents are built in an isolated builder workflow, not on the application host.
- Runtime instances can only interact through the broker and approved platform adapters.
