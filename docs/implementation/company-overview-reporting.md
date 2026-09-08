# Company overview reporting

The overview uses full-width rows. Order is stored in `CompanyDashboardLayouts` per organization and signed-in organization member. Defaults: approvals, finance, legal, projects. Apply the `AddCompanyDashboard` migration before serving the updated API (normal application database initialization applies migrations).

Finance/legal reports are durable, append-only snapshots. Their author and publication time come from the authenticated agent identity and server clock. A report remains visible after its author is disabled or removed. Unknown numbers stay null, not zero. Each finance report is a complete snapshot for the month containing `asOf`; amounts are month-to-date except cash balance (as of that date) and monthly budget. A prior-month report is explicitly labeled. Legal obligations are the current outstanding list, including overdue items; publishing an empty list means none were reported.

A compatible CFO, CLO, or third-party agent must be an active employee with an enabled, ready installation in this organization and an approved reporting capability in its manifest `requires` / required grants. Job titles alone do not authorize publishing. Add `modelVisible: true` to a requirement when an LLM should discover the tool. Existing grants are never automatically expanded.

| Required capability | Model tool | Input |
|---|---|---|
| `platform.company.finance-report.v1` | `publish_company_finances` | `asOf` (YYYY-MM-DD), `currency` (three uppercase letters), optional nullable `revenue`, `expenses`, `cashBalance`, `monthlyBudget`; at least one number required |
| `platform.company.legal-report.v1` | `publish_company_legal_status` | `entityName`, `entityType`, `status`, `verifiedOn` (YYYY-MM-DD), `obligations` array of `{ title, dueDate }` |
| `platform.company.project-update.v1` | `publish_project_lead_update` | `workstreamId`, `summary` (1–2,000 characters); caller must be the project's accountable lead |

The organization is resolved from the authenticated session, never supplied in the report. First publication replaces onboarding with real data. With no capable employee, widgets link to hiring; with capable employees but no report, they link to those employees. Project updates are explicitly scoped, so a manager's generic check-in or a project's objective is never displayed as its latest update.

Human reads: `GET /api/core/organizations/{id}/dashboard` (manager or owner), `GET/PUT /api/core/organizations/{id}/dashboard/layout` (active member, own layout only). PUT accepts `{ "order": ["approvals", "finance", "legal", "projects"] }` with all four identifiers exactly once. Existing authorized portfolio inspection includes an optional `latestLeadUpdate` per project. Briefing configuration/history remain available at `/organizations/{id}/executive-briefings` through the overview's Briefing settings link.
