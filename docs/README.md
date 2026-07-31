# C-Sweet Documentation

This index covers the main product, architecture, runtime, deployment, marketplace, security, and delivery documentation.

The normative plugin contract, grant lifecycle, proxy policy, administrator runbook, and readiness checklist are in [`plugin-platform/README.md`](plugin-platform/README.md).

## Recommended reading order

1. `00-product-vision.md`
2. `01-domain-model.md`
3. `02-agent-orchestration.md`
4. `03-workforce-marketplace.md`
5. `04-remote-worker-provider-protocol.md`
6. `05-human-workforce.md`
7. `06-budgeting-and-governance.md`
8. `07-security-privacy-and-trust.md`
9. `08-application-architecture.md`
10. `09-prototype-roadmap.md`
11. `10-open-questions.md`
12. `11-brand-and-naming.md`
13. `12-example-scenarios.md`
14. `13-system-boundaries-and-deployment.md`
15. `14-application-design-system.md`

## Product and platform guides

- [Docker deployment](deployment/docker.md)
- [Marketplace integration](MARKETPLACE_INTEGRATION.md)
- [Plugin platform](plugin-platform/README.md)
- [Implementation plans](implementation/README.md)
- [Legacy and dead-code audit](analysis/legacy-and-dead-code-audit.md)

## Agent runtime, security, and operations

- [MCP agent runtime architecture](../Documentation/Architecture/MCP_AGENT_RUNTIME.md)
- [Software Developer runtime](../Documentation/Architecture/SOFTWARE_DEVELOPER_RUNTIME.md)
- [Brokered MCP and hiring](../Documentation/BROKERED_MCP_AND_HIRING.md)
- [Agent runtime threat model](../Documentation/Security/AGENT_RUNTIME_THREAT_MODEL.md)
- [MCP-only agent migration](../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md)
- [Agent runtime operations runbook](../Documentation/Operations/MCP_AGENT_RUNTIME_RUNBOOK.md)

## Maintenance guidance

- Update `10-open-questions.md` when assumptions become decisions.
- Create ADRs for decisions with significant architectural consequences.
- Keep marketplace-provider contracts separate from internal domain models.
- Keep Microsoft Agent Framework behind application-owned abstractions.
- Add scenario tests for every major workflow introduced.
- Keep `14-application-design-system.md` and the shared `--cs-*` CSS tokens synchronized for every visual-system change.
- Treat these documents as living plans rather than immutable specifications.
