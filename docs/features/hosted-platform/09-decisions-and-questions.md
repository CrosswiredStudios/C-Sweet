# Decision register and review questions

[Overview](README.md) · [Pricing](06-pricing-and-economics.md) · [Acceptance](10-acceptance-scenarios.md)

**Status: Living review register.** Last updated: **2026-10-15**.

An agreed direction records an explicit user choice. A proposed default records a
recommendation, not consent. An observed capability records source evidence, not
a live deployment test. Open details intentionally remain visible for review.

## Agreed direction

| ID | Decision | Context / implication |
|---|---|---|
| D-01 | Use `www.c-sweet.com` as the hosted product entry | Public routing comes after private development |
| D-02 | Serve different customer sizes through tiers | Do not restrict the product to enterprise buyers or solo users |
| D-03 | Separate Headquarters and Office offerings | Customers can link multiple Offices and choose who operates them |
| D-04 | Shared underlying servers are acceptable | Maintain separate customer deployment/data boundaries |
| D-05 | Support managed inference and BYOK | Billing and credentials must distinguish the payer |
| D-06 | Support customer-owned Offices without exposed ports | Build on outbound Office sessions; no customer VPN prerequisite |
| D-07 | Support private models through a standalone connector | Model-only installation must not require Office/Hyper-V |
| D-08 | Charge a smaller fee for customer-owned Offices | Exact fee is not yet selected |
| D-09 | Charge the full agent fee if the agent works in the month | Detailed activity trigger and adjustment policy still need review |
| D-10 | Hosted Offices may include hours and approved overage | No unrestricted compute promise |
| D-11 | Entry hosted Offices may sleep | Explain availability and startup latency |
| D-12 | Aim for a useful entry package around $100–$200 before tokens | Validate against actual operating cost and support |
| D-13 | Start development on the owned Windows PC | Reported 32 GB RAM, approximately 2 TB SSD, Tailscale; hardware uninspected |
| D-14 | Keep hosting portable and move to US managed cloud later when justified | AWS is a candidate destination, not a prerequisite for development |
| D-15 | Write reviewable feature documentation before implementation | This folder remains product/design context, not a delivery status claim |

The earlier $500–$2,000 pilot infrastructure range was a selected planning scale,
not authorization to spend it now. Later discussion specifically prioritized
avoiding rental costs during development. The proposed $50/$150 or $100/$300
development/launch budget choices were not answered and are not approved budgets.

## Proposed defaults, not yet adopted decisions

| ID | Proposal | Rationale | Where to review |
|---|---|---|---|
| P-01 | $99 Standard HQ, $9 linked own Office, $10 active agent | $138 example entry bill; test margin and willingness to pay | [Economics](06-pricing-and-economics.md) |
| P-02 | $69 / $249 hosted Offices with 100 / 300 active hours | Separate seats from execution capacity | [Economics](06-pricing-and-economics.md) |
| P-03 | Managed token markup of 30% and prepaid balance | Limit unfunded provider exposure; disclose actual margin | [Economics](06-pricing-and-economics.md) |
| P-04 | Separate app deployment and database per Headquarters | Reduce dependence on retrofitting business scopes into tenant isolation | [Architecture](02-architecture-and-tenancy.md) |
| P-05 | One Headquarters assignment per Office/connector identity | Align with existing Office trust ownership | [Connectivity](03-office-connectivity.md) |
| P-06 | Scoped gRPC model relay with local endpoint approval | Reuse transport experience without exposing the LAN | [Models](04-model-connectivity.md) |
| P-07 | No automatic paid or cross-location fallback by default | Preserve cost and data-routing intent | [Models](04-model-connectivity.md) |
| P-08 | Two / ten model connectors included by HQ tier | Avoid a separate fee merely for a model-only computer | [Economics](06-pricing-and-economics.md) |
| P-09 | $50/month managed-model test cap | Keep routine tests deterministic and cheap | [Owned server](07-owned-server-validation.md) |
| P-10 | OIDC-capable account boundary | Keep identity provider replaceable | [Architecture](02-architecture-and-tenancy.md) |
| P-11 | Ten idle minutes before sleeping an entry hosted Office | Reduce idle cost while avoiding immediate restart churn | [Economics](06-pricing-and-economics.md) |

## Open questions and decision consequences

### Q-01 — Final prices, tiers, and staffing economics

**Question:** Are the proposed subscription prices and inclusions acceptable once
we measure capacity, support, and willingness to pay?

**Recommended review:** keep the $138/$198 examples as hypotheses. Compare measured
owned/budget-host economics against the AWS reference before any price guarantee.
Decide whether pricing is per Headquarters, per customer account, or permits an
account to purchase multiple independently priced Headquarters instances.

**Blocks:** public price list, production subscription SKUs, and margin promises.

### Q-02 — Availability and support commitment

**Question:** What support hours, response expectations, maintenance windows,
backup retention, and recovery objectives can C-Sweet actually operate?

**Recommended review:** private alpha without a contractual SLA; measure restore
and operator effort first. The earlier four-hour recovery and fifteen-minute
database recovery-point examples are not adopted commitments.

**Blocks:** paid availability/support promises, production backup sizing.

### Q-03 — Exact billing events and lifecycle policies

**Question:** What starts an active-agent fee: admitted execution, first real work,
or successful completion? How do automatic background work, immediate failures,
mid-month linking/unlinking, refunds, and unpaid invoices behave?

**Recommended review:** retain the accepted full-month active-agent principle;
choose a durable auditable activation event with owner-authorized staffing budget.
Agree calendar versus subscription-anniversary periods and one billing timezone.
Specify what happens to in-flight work when a budget or entitlement expires.

**Blocks:** financial acceptance tests and production invoice generation.

### Q-04 — Privacy, retention, and operator access

**Question:** Which data is retained, for how long, who can access it for support,
and when does deleted data expire from backups?

**Recommended review:** minimize default diagnostic content and require audited,
bounded support access. Document local-inference data transit honestly. Obtain
applicable business/legal review instead of copying a generic compliance claim.

**Blocks:** customer terms, retention controls, and security-sensitive onboarding.

### Q-05 — Initial provider/model support

**Question:** Which exact provider APIs, model versions, and required capabilities
will the first service advertise?

**Recommended review:** evaluate a small economical/standard/premium set using
representative C-Sweet tasks. Add native adapters where needed; do not equate an
enum entry or model-list response with working agent compatibility.

**Blocks:** supported-model catalog and default model assignments.

### Q-06 — Connector protocol and service limits

**Question:** What are the concrete frame/request limits, maximum streaming
duration, concurrency, queue bounds, retention of dedupe receipts, and relay quota?

**Recommended review:** test large contexts, embeddings, cancellation, slow readers,
and disconnects before freezing wire contracts. Decide package ownership for the
standalone connector independently of the Office runtime. Decide whether a later
WebSocket transport is needed for restrictive networks.

**Blocks:** connector protocol release and a priced relay service.

### Q-07 — Initial private endpoint scope

**Question:** Ship loopback-only first, or locally approved LAN endpoints as well?

**Recommended review:** loopback default; explicitly approved LAN endpoint support
only with destination revalidation, redirect rejection, and negative tests. No
network discovery or arbitrary tunnel. Decide whether endpoint changes require
both local and Headquarters owner approval.

**Blocks:** private connector security acceptance.

### Q-08 — Public staging ingress

**Question:** How do we test public Office/model connectivity while Headquarters
still runs on the owned PC?

**Recommended review:** compare a temporary public gateway with restricted private
backhaul against a short-lived complete hosted staging deployment. Test client
certificates, HTTP/2, renewal, affinity, and stream timeouts. Keep operator VPN use
separate from customer requirements.

**Blocks:** no-Tailscale customer connectivity claim; does not block private development.

### Q-09 — First paid hosting environment

**Question:** Does measured demand justify a budget US host, or should the service
move directly to AWS? What is the dated bill of materials and operating burden?

**Recommended review:** choose after owned-server measurements. A BYO-first offer
can use ordinary app hosting; hosted Offices need separately certified capacity.
Reconfirm region, current rates, stock, setup fees, and backup strategy.

**Blocks:** infrastructure purchase and paid capacity commitments.

### Q-10 — Owned-PC packaging and identity provider

**Question:** Which unattended PostgreSQL packaging and account identity provider
fit the installed Windows deployment and later cloud operation?

**Recommended review:** establish clean reboot/service behavior before adding more
tenants. Keep OIDC integration and database deployment behind configuration/adapters.
Inspect CPU, free disk, and existing services before reserving capacity.

**Blocks:** final installation runbook; does not block feature review or mock/test flows.

## Review log

| Date | Change in direction | Reason |
|---|---|---|
| 2026-10-15 | Began with per-customer hosted HQ and Offices | Establish service and economics |
| 2026-10-15 | Deferred AWS spending; selected owned-PC validation | Reduce development/testing costs |
| 2026-10-15 | Made customer-owned Offices and private models first-class | Expected customer preference and lower execution expense |
| 2026-10-15 | Chose standalone model connector and smaller own-Office fee | Separate inference hosting from agent runtime |
| 2026-10-15 | Created feature documentation for iteration | High-level chat plans lacked sufficient context and examples |

When a question is resolved, record its decision, date, rationale, and affected
documents here. Promote significant architectural decisions to the repository's
ADR convention when delivery begins; do not silently erase the original tradeoff.
