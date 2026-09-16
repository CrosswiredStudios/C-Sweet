# Chief of Staff workforce platform

The Chief of Staff is a required leadership position, not a privileged agent implementation. New organizations begin in `Draft`; activation requires one active `chief-of-staff` leadership assignment. The configured C-Sweet Chief is a suggested GitHub catalog entry and follows the same preview, grant, installation, employee-binding, and audit path as any imported agent.

## Authority boundaries

- C-Sweet owns profiles, organization state, proposals, budgets, reservations, management cycles, and audit records.
- C-Sweet may supply transport metadata and universal interaction contracts, but it does not inject a Chief-of-Staff persona, response policy, or operating prompt. Those behaviors belong to the installed agent package so competing agents can differentiate themselves.
- Agents receive only explicitly granted, organization-scoped broker capabilities.
- Explicit low-risk owner facts require verified conversation/message provenance and an expected revision.
- Inferred or sensitive business changes, finance changes, workstreams, workforce plans, and staffing actions become proposals.
- Workforce-plan approval does not install an agent, expand grants, reserve/spend money, contact a human, or accept an engagement.
- Paid execution requires a same-currency budget reservation; the most restrictive applicable budget wins.

## Ranked hiring backlog

The platform exposes an optional, installation-scoped hiring backlog rather than prescribing how an agent must use it. An authorized agent can read its own role recommendations, create candidate-free role to-dos, assign explicit priorities, attach ranked candidates later, and stage a selected candidate for owner approval. The platform persists and enforces the records; the plugin decides how to assess the organization, rank roles, and conduct the hiring conversation.

Withdrawing a recommendation retires its pending Marketplace suggestions immediately, and creating a replacement suggestion supersedes the old widget with "replaced by" lineage. One actionable hiring suggestion is kept per conversation; multi-role batches created together stay grouped. See [Hiring suggestion lifecycle](./features/hiring-suggestion-lifecycle.md).

## Extension points

The public Agent SDK exposes typed platform clients, structured failures, Agent Framework tool adapters, activation hooks, `IBusinessPatternProvider`, `IWorkforceRouter`, and `IWorkforceCatalogProvider`. Catalog providers declare whether they supply suggested agents, digital marketplace offerings, hybrid offerings, or verified humans. The router searches current staff and installed agents first, then digital catalogs, and only uses human catalogs as a fallback unless a human is mandatory.

An installation with no connected marketplace returns an explicit unavailable result. It never fabricates workers, availability, credentials, or prices.

## Management cadence

`ManagementReviewScheduler` reads persisted management cycles, creates inbox check-ins along reporting lines, emits granted review events, sends one reminder, and marks unanswered requests stale after one day. Structured status and resource-need events are persisted and audited. The first-party Chief loads authoritative profile, finance, organization, pattern, and cadence state for every turn; memory is continuity context only.

External email/chat check-ins, marketplace fulfillment, human outreach and acceptance, milestone charging, and FX conversion remain adapter concerns. No connector or marketplace is implied when one is not configured.

## Proactive executive briefings

Executive briefings are durable `ExecutiveBriefing` management requests created after organization activation, after a new Chief runtime instance, on the configured daily/weekday/weekly schedule, or from the Command Center test action. Broker reconnects reuse the same runtime identifier, and a one-hour startup cooldown prevents crash-loop spam.

The Chief returns structured status plus concise Markdown using the existing granted management events. The platform matches the exact request, validates the Markdown, and resolves the Chief's current reporting manager at delivery time. Human managers receive one in-app conversation message and notification; managing agents receive a targeted broker event. Delivery history, retry state, and failures remain visible in the Command Center. Scheduled and startup requests respect organization quiet hours, while an explicit manual test runs immediately.
