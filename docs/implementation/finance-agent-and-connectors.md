# Finance Agent and financial connectors

Status: Draft for iteration. Updated November 25, 2026.

C-Sweet should offer a Finance Agent that can monitor cash, notify business owners, and prepare
reports using financial data from plugins. SimpleFIN Bridge is the first bank data provider. The
Finance Agent and SimpleFIN connector are free to install. Customers buy their SimpleFIN subscription
directly and authorize C-Sweet with a one-time setup token. The first release focuses on read-only cash
monitoring and tax preparation support for U.S. businesses.

Over time, C-Sweet can own the books for businesses that want a replacement for QuickBooks. Businesses
can keep their existing checkout, billing, or payroll systems. Banks, payment providers, and accounting
vendors can publish their own connectors and agents as the platform grows.

## Provider selection and business access

1. **Install at the enterprise level.** Install a provider plugin once. During setup, an authorized
   enterprise admin chooses the plugin that supplies a capability when more than one approved plugin
   offers it. Require an explicit choice to keep the current provider or switch when a new candidate
   creates a conflict. Select related bank account, balance, and transaction tools together so they use
   the same account identifiers and connection.
2. **Use the enterprise choice as the business default.** Allow an authorized business-level override
   for clients who use a different provider. When a different provider is selected, require that
   business to authorize the new connection and accounts before it can sync. A missing, stale, or
   revoked binding results in an actionable status for an authorized operator. Provider changes
   require an explicit selection.
3. **Connect and grant access inside the business.** The person authorizing access chooses the
   connected accounts the Finance Agent can read. The host checks the current business, agent,
   connection, account, and requested operation on every call. An enterprise provider choice grants
   eligibility to use the plugin. Financial access requires the business-scoped grant.
4. **Enable feeds that serve distinct purposes together.** SimpleFIN can supply the bank feed while
   Stripe supplies sales and payouts. A business can connect both for reconciliation. If two feeds
   contain the same bank activity during a migration, reconcile duplicate records before using both
   in reports.

## SimpleFIN setup and MSPs

An authorized business user links accounts in SimpleFIN Bridge and pastes the setup token into a
protected C-Sweet connection form. C-Sweet claims it once and stores the returned access credential
in protected storage. The connector validates the claim and access endpoints before contacting them.
Credentials stay in the connection service or host vault. Agents receive account references and
authorized data. After setup, the business administrator reviews the discovered accounts and grants
access to the selected accounts. [SimpleFIN developer guide](https://beta-bridge.simplefin.org/info/developers)

Support multiple connections per business as well as connections across businesses. One Bridge
subscription can cover multiple linked banks and apps. SimpleFIN advertises up to 25 institutions and
25 apps on a subscription, at $1.50 per month or $15 per year plus tax as of November 25, 2026.
That limit applies to the Bridge subscription. Each Bridge credential may expose multiple accounts,
so the connector must enforce C-Sweet's account grants even when the provider returns a broader set.
[SimpleFIN pricing and limits](https://bridge.simplefin.org/) · [SimpleFIN data-sharing policy](https://beta-bridge.simplefin.org/info/privacy)

For managed service providers (MSPs), use client-owned subscriptions by default. Client A can connect
one subscription and Client B another through the same enterprise installation. A client with several
connections can authorize each independently. The client owner can authorize setup directly or
delegate setup to an MSP staff member whose permissions cover that business. An MSP operator can see
connection health across authorized clients while each Finance Agent run stays scoped to its business.
Offboarding removes the MSP's delegated access and leaves the client in control of its connection,
sync history, and reports. Pilot this with two clients and test both directions of the access boundary.

## Tool contract and synchronization

Define versioned, provider-neutral read capabilities for listing granted financial accounts, reading
balances, and listing transactions for an account and date range. Normalize amounts and currency,
stable account and transaction references, balance timestamps, posting dates and states, pagination,
and source provenance. Include freshness and partial-sync information so cash reports can show the
period they cover. Use stable source identities to make repeated pulls idempotent. In SimpleFIN v2,
account IDs are scoped to a connection, so use both identifiers when resolving source accounts.
[SimpleFIN protocol](https://www.simplefin.org/protocol.html)

Keep credential exchange, discovery, provider requests, normalization, and grant enforcement outside
the model. The Finance Agent calls the tools, explains findings, and suggests categories for review.
Platform code computes totals, threshold crossings, and source identities. The host owns the provider
binding and the authoritative status of each connection and sync.

Schedule provider sync in a durable platform worker, with daily refresh as the initial SimpleFIN
cadence. Overlap recent transaction windows for late postings and keep backfill requests within
provider date-range and quota limits. Retry transient failures within those limits, keep the latest
successful data visible with its timestamp, and surface access revocation or stale data to the
business owner. Commit new data and any notification outbox record together. Agent events are wake
hints. On restart or redelivery, the agent reads authorized current state and uses an idempotent
notification effect. Track which accounts completed a sync so a partial result cannot make missing
data look current. [SimpleFIN sync guidance](https://beta-bridge.simplefin.org/info/developers)

## Implementation milestones

### 1. Connect and authorize

Extend the dependency model to bind a versioned capability to an approved provider while preserving
existing package-specific dependencies. Reuse the explicit selection and business grants in
[`ConnectorBindingService`](../../src/CSweet.Infrastructure/Setup/ConnectorBindingService.cs) and
[`PluginAccountAuthorityService`](../../src/CSweet.Infrastructure/Setup/PluginAccountAuthorityService.cs).
[`PluginDependencyDeclaration`](../../src/CSweet.Contracts/Plugins/ConnectorContracts.cs) currently
names an exact plugin and publisher. Reuse the existing protected secret store and connector execution
checks. Determine how the SimpleFIN claim and access URLs fit the current transport before choosing
the adapter seam. [Managed connection service plan](managed-connection-service-plan.md)

Accept this milestone when a business can claim a setup token, authorize selected accounts, revoke
access, and see connection health. Demonstrate explicit selection between two providers with the
same contract, test two businesses with independent credentials, and test one business with multiple
connections. Pilot with a real Wells Fargo account of the intended business type.

### 2. Monitor cash and prepare reports

Publish the Finance Agent with SimpleFIN as the initial supported provider. Deliver a cash activity
summary, current and available balances when supplied, low-balance alerts, and stale-connection
notices. Let the business set alert thresholds. Give the owner or accountant a reviewable transaction
export and a list of uncategorized activity or missing support for tax preparation. Keep bank-derived
cash reports distinct from ledger-based financial statements. Deposits can represent loans, transfers,
or net payouts. Tax preparation also needs supporting records and reviewed classifications.
[IRS recordkeeping guidance](https://www.irs.gov/businesses/small-businesses-self-employed/what-kind-of-records-should-i-keep)

Accept this milestone when scheduled sync works across a restart, repeated imports avoid duplicate
transactions, and a replayed wake yields one notification for the same threshold crossing. Use
deterministic `AgentTestHost` scenarios tagged `Category=AgentHarness` for agent dispatch, reports,
and durable alerts. Exercise connector authorization, expiry, revocation, and tenant isolation in
platform tests. [Agent testing framework](agent-testing-framework.md)

### 3. Reconcile payments and build the books

For a bakery using Stripe Checkout, connect Stripe to import gross sales, refunds, fees, and payout
details. Match each payout to the bank deposit and record it as a settlement of the Stripe balance.
Keep unmatched items available for review. Add the bookkeeping records needed for a double-entry
ledger, reconciliation, and later financial statements. Preserve CSV import for fallback and backfill.
[Stripe payout reconciliation](https://docs.stripe.com/reports/payout-reconciliation)

Accept this milestone when a sample batch with $100 in sales, $3 in fees, and a $97 bank deposit
reconciles to one sale, one fee, and one settlement. Connect a second provider without changing the
Finance Agent's bank read contract. Use the same books for businesses with different checkout tools.

### 4. Add optional invoicing and filing workflows

Offer native invoicing for businesses that bill customers directly. Use customer records, numbered
invoices, an authorized sender, and auditable delivery and payment status. Send email through the
business's configured mailbox within its plan limits. Offer automated SMS through a customer-owned
messaging provider that handles carrier fees and applicable registration. Businesses with an existing
billing system can continue to feed its activity into the same books. [U.S. SMS pricing and fees](https://www.twilio.com/en-us/sms/pricing/us)

Extend tax support from an owner- or accountant-reviewed packet to specific document workflows as
jurisdictions and entity types are selected. Add submission through the appropriate authorized
provider or agency process, with human sign-off and retained filing acknowledgments. Payroll and
sales tax data enter through their respective records and providers. [IRS business e-file](https://www.irs.gov/e-file-providers/e-file-for-business-and-self-employed-taxpayers)

## Decisions for the pilot

- Choose the contract versioning and provider conformance checks for account IDs, timestamps,
  pending transactions, and pagination.
- Verify SimpleFIN coverage and revocation on the intended Wells Fargo business account. Confirm
  the hosted data processing terms and the connection limits that matter for MSP onboarding.
- Set the bank history retention period, report export format, and offboarding policy for client
  data and credentials.
- Choose the first bookkeeping reports and tax packet fields with a pilot business or accountant
  before extending into filing integrations.
