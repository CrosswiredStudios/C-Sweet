# Unified Workforce Marketplace

## Purpose

The marketplace is a unified catalog of labor and capability. It should support local AI workers, remote commercial workers, human professionals, agencies, and hybrid services.

The marketplace is optional for self-hosted installations. The open-source core must support included workers and direct provider connections without requiring the official marketplace.

## First-party agent sources

The checked-in catalog is `src/CSweet.Api/first-party-agents.json`. Every first-party agent must have an available listing with its hosted GitHub repository URL, agent ID, role, and specializations. Hiring previews the repository manifest and pins the resolved commit before building through Office, as it does for Chief of Staff. The project's build path comes from that repository's manifest.

Hiring does not scan local agent folders or sibling checkouts. Add new agents to this catalog when their repositories are ready; a local checkout alone does not make an agent available for hiring. Refresh candidate searches after updating the catalog; previously selected local-source candidates need to be replaced with the first-party listing.
## Marketplace offering types

### Local open-source worker

- Downloadable and inspectable definition
- Runs through the user’s selected local or hosted model
- May declare required MCP servers or tools
- Can operate offline when dependencies are local
- Usually free
- Can be distributed through GitHub, a registry, or the official marketplace

### Remote commercial worker

- Executes on the publisher’s infrastructure
- May use proprietary models, prompts, data, code, and MCP services
- Requires network access to the publisher
- Publisher enforces licensing and billing
- Local installation contains a read-only descriptor and connection configuration
- May be offered by a large software vendor or a small specialist provider

### Human professional

- Real person with a profile, skills, availability, credentials, rates, and reviews
- Must accept an engagement before becoming active company staff
- Can work hourly, fixed price, by milestone, on retainer, or through packaged services
- May delegate preparation work to authorized agents

### Hybrid service

- Provider-managed combination of software, AI agents, and human work
- May expose multiple roles under one provider workspace
- Must disclose execution composition and human-review behavior
- May charge for software usage, professional review, or both

## Workforce provider

A provider may publish several related offerings and share authentication, billing, data connections, and service policies.

Example:

```text
Intuit Workforce Provider
├── Bookkeeper
├── Accountant
├── Tax Professional
├── Payroll Specialist
└── Expense Auditor
```

A future provider such as Intuit could expose accounting and tax workers backed by proprietary models, rules engines, connected financial data, MCP servers, and optional human professionals.

## Marketplace entities

- `PublisherAccount`
- `PublisherMember`
- `PublisherVerification`
- `WorkerOffering`
- `OfferingVersion`
- `MarketplaceListing`
- `ListingCategory`
- `CapabilityClaim`
- `PricingPlan`
- `BillingMeter`
- `Entitlement`
- `ProviderConnection`
- `Installation`
- `ProfessionalProfile`
- `Engagement`
- `Review`
- `Rating`
- `Report`
- `ModerationCase`
- `Certification`
- `CompatibilityTest`

## Important distinctions

```text
Offering       Product or service advertised by a publisher
Version        Immutable machine-executed implementation version
Listing        Public presentation and sales page
Entitlement    Company’s right to use a commercial offering
Connection     Authenticated link to a provider workspace
Engagement     Accepted agreement with a human or managed service
Staff Member   Company-specific worker instance
```

## Marketplace installation sources

Self-hosted users should be able to add workers through:

1. Official marketplace
2. Direct manifest URL
3. Manually uploaded descriptor
4. Private enterprise registry
5. Included source-controlled role pack

The official marketplace should not be a runtime dependency for locally installed workers.

## Search and recommendations

Marketplace search should filter and rank by:

- Capability
- Resource type
- Price and pricing model
- Availability
- Location and time zone
- Credentials and jurisdiction
- Local versus remote execution
- Data-processing policy
- Model and tool requirements
- Reviews and verified performance
- Provider reliability
- Compatibility with company policies

The Personal Assistant should be able to search the marketplace and present staffing options. Financial commitments require configured authority or approval.

## Pricing models

Potential models include:

- Free
- One-time license
- Monthly or annual subscription
- Per company
- Per active worker
- Per task
- Per token
- Per transaction or functionality unit
- Hourly
- Fixed price
- Milestone
- Retainer
- Paid consultation
- Custom enterprise contract

The marketplace should store pricing disclosures.

Remote providers remain authoritative for their own usage billing. Human engagements use platform contracts and payment records.

## Service meters

Remote providers may define billable units such as:

- Model tokens
- Transactions categorized
- Accounts reconciled
- Reports generated
- Human reviews
- Payroll runs
- Forms prepared
- Images generated
- Build minutes
- Premium data queries

The core budgeting system should treat meters as named quantities with optional monetary estimates and limits.

## Reviews and reputation

Reputation should include structured dimensions:

- Work quality
- Communication
- Timeliness
- Reliability
- Instruction adherence
- Cost accuracy
- Rework frequency
- Completion rate
- Rehire or renewal rate
- Dispute rate
- Credential status
- Provider availability

Reviews should be tied to verified use. Public performance metrics must have clear definitions and context.

## Credentials

The system must distinguish:

- Self-declared
- Provider-asserted
- Marketplace-verified
- Government or credential-provider verified

Professional offerings should identify:

- Jurisdiction
- Expiration
- Permitted service scope
- Verification source

A role title alone must never imply a verified license.

## Trust and execution disclosures

Every listing should clearly show:

- Execution type
- Where data is processed
- Whether data leaves the self-hosted environment
- Retention and training policy
- Required permissions
- Required tools and provider connections
- Whether humans may access the data
- Typical response time
- Service availability expectations
- Pricing basis

## Versioning and updates

Machine-executed versions are immutable.

An update that requests new permissions requires company approval.

Companies should choose among:

- Pinned version
- Manual updates
- Automatic patch updates
- Automatic minor updates
- Preview channel

The marketplace may delist or disable compromised versions while preserving customer history and artifacts.

## Marketplace moderation

Submission pipeline:

1. Draft
2. Identity or publisher verification
3. Descriptor and package validation
4. Permission and data-policy review
5. Compatibility testing
6. Behavioral evaluation where applicable
7. Human marketplace review
8. Publication

Potential automated checks include:

- Embedded secrets
- Undeclared network access
- Schema compliance
- Permission escalation
- Runaway execution
- Deceptive claims
- Compatibility
- Billing-meter consistency
- Credential validity

## Hosted and open-source boundaries

Open-source core responsibilities:

- Workforce abstractions
- Provider and professional contracts
- Direct installation
- Company staffing
- Task orchestration
- Budget enforcement
- Local audit history

Likely hosted marketplace responsibilities:

- Discovery and search
- Publisher accounts
- Identity verification
- Paid transactions
- Payouts
- Reviews
- Disputes
- Moderation
- Service-health aggregation
- Credential verification

## Marketplace principle

The marketplace should answer:

> What combination of local agents, commercial services, and real professionals can complete this work within the company’s budget, permissions, schedule, quality, and risk requirements?
