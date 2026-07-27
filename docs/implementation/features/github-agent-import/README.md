# GitHub Agent Import

> **Superseded historical design.** This document describes the retired import/runtime contract.
> Use the [MCP-only migration](../../../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md)
> and [current architecture](../../../../Documentation/Architecture/MCP_AGENT_RUNTIME.md).

## Goal

Allow a user to import an agent from a public GitHub repository by URL, inspect the agent's root
manifest, approve requested permissions, and run the agent through the existing broker contract.

The import path must treat the repository as untrusted code. A GitHub URL is a source location,
not an authority grant.

## Current runtime state

Today, the newer agent architecture exists in the Aspire developer path:

- `CSweet.AgentHost` is a trusted gRPC broker.
- `CSweet.Agents.PersonalAssistant` is a separately executable agent.
- Agents register through the broker using `csweet-agent.json`.
- The broker applies configured deny-by-default grants.

The Docker Compose distribution currently lags that model. It includes app, API, worker, migrator,
and Postgres services, but not yet `CSweet.AgentHost` or a separately containerized Personal
Assistant service. Before GitHub-imported agents are enabled, the Compose path should include the
broker and first-party agent services as a baseline.

## Feature docs

1. [Import and Sandbox Architecture](./01-import-and-sandbox-architecture.md)
2. [Task Checklist](./02-task-checklist.md)

For scheduling, always-on agents, ephemeral runtime containers, .NET source builds, and global
container settings, use the companion
[Agent Runtime Manager](../agent-runtime-manager/README.md) plan.

## Non-goals for the first version

- Running arbitrary downloaded source code directly on the application host.
- Granting repository-declared permissions automatically.
- Supporting private repositories.
- Supporting paid marketplace billing.
- Supporting silent automatic agent updates.
- Giving agents direct database, filesystem, Docker socket, RabbitMQ, or secret-store access.

## Definition of done

- A user can paste a public GitHub repository URL.
- C-Sweet reads and validates a root `csweet-agent.json`.
- C-Sweet records an immutable imported package version by commit SHA and manifest digest.
- The admin can approve a bounded grant that is no broader than the manifest request.
- The agent runs as an isolated workload and can only interact through the broker.
- Network, filesystem, secret, MCP, and API access are mediated by platform-controlled adapters.
- Revocation disables future runs without deleting historical artifacts or audit records.
# Superseded historical design

> This document describes the retired gRPC import/runtime model. See [MCP-only migration](../../../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md) for the current protocol-v2 policy.
