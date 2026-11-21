# Purchasing a hosted Headquarters through Marketplace

**Proposal for review, November 20, 2026.** A customer visits www.c-sweet.com, buys a Headquarters subscription and opens their workspace from their Market account. This document describes the proposed purchase and fulfillment flow.

## The first purchase

[Open the four-page interactive design proposal](design/purchase.html) or compare
the [Aurora Light board](design/aurora-pages.webp) and [Black & Gold board](design/gold-pages.webp).
In the prototype, Page selects a screen and Scenario previews billing and provisioning
outcomes. The review toolbar, sample purchaser and simulated handoffs belong to the prototype.

| Proposed Market route | Customer and entry point | Main action |
|---|---|---|
| `/hosting` | Public visitor following Hosting in Market navigation | Choose **Build Plan** to enter the builder |
| `/hosting/review` | Authenticated buyer | Adjust the quantity of each Office type, see the monthly total and continue to payment |
| `/account/hosting/orders/{orderId}` | Buyer returning from Checkout or reopening an order | See payment and setup independently, resume an unpaid checkout or open a ready workspace |
| `/account/headquarters` | Returning owner following My Headquarters | Find the workspace, open its order or manage billing |

**Existing navigation proposal.** Insert **Hosting** between **Documentation** and
**Marketplace** in `NavMenu.razor`, including its mobile menu. In the signed-in
menu, offer **My Headquarters** alongside **Account**. These are link additions
within the existing Market header. The four hosting routes use the themed header
shown in the preview. Existing sign-in, registration, Stripe Checkout and the
billing portal are handoffs. The final workspace link opens Headquarters itself.

**Build Plan** navigates directly to the protected builder. Signed-out visitors, including those opening its URL directly, go through Market sign-in and return to the builder. The local prototype simulates a session with a sample account. The live Market route and checkout endpoint must enforce authentication on the server and bind orders to the authenticated owner.

Maya selects two of her own computers and one Standard hosted Office. Her monthly subtotal is $186 ($99 + 2 × $9 + $69). Removing either computer immediately reduces the subtotal by $9. Market will resolve the selected quantities against current offers on the server and freeze the accepted line items for payment and fulfillment.

Maya reviews the monthly Headquarters price of $99, renewal date and any charges that connecting an Office or activating employees would add. She can start with her own PC and API key. She can buy Headquarters before installing either. Compare the proposed hosted Office resources, hard concurrency caps and prices in [pricing and economics](06-pricing-and-economics.md).

After Checkout, **My Headquarters** shows payment and setup progress. Maya can close the tab and return later. When Hosting confirms the workspace is healthy and its owner is assigned, the page shows a clickable workspace address such as **northstar.csweet.app**. Inside Headquarters she links an Office and chooses a model. If setup stalls, the same page shows the order reference, the next action and who is handling it. Asking a paid customer to check out again would risk a second subscription.

## What Marketplace can reuse

The current Market site has a self-hosting guide at `/docs/hosting`. Give the hosted offer its own entry and let visitors choose between the two paths. [HostingDocs.razor](../../../../CSweet.Market/src/CSweet.Market.Web/CSweet.Market.Web/Components/Pages/HostingDocs.razor)

Market already authenticates purchasers, creates orders before Checkout, processes signed payment events and opens a customer billing portal. Its existing monthly “per instance” purchase licenses a publisher's listing. Checkout requires a listing and routes proceeds to the publisher's connected account. The existing refund path also accounts for a seller transfer. A first-party hosting subscription needs its own offer, entitlement and fulfillment handler, with C-Sweet receiving the payment. Reuse the payment and identity infrastructure where those rules fit. [MarketplaceCommerceService.CreateCheckoutAsync, ProcessWebhookAsync and CreateRefundAsync](../../../../CSweet.Market/src/CSweet.Market.Web/CSweet.Market.Web/Services/MarketplaceCommerceService.cs) · [Market commerce design](../../../../CSweet.Market/docs/STRIPE_COMMERCE.md)

## Decisions to settle

| Decision | Proposed starting point | When it matters |
|---|---|---|
| What does one purchase buy? | One Headquarters plus the selected quantities of customer-owned Office connections and hosted Office tiers. Define whether an owner can deliberately buy a second workspace. Use the $99 monthly price in test mode. | Before designing Checkout and duplicate-order rules |
| Who can buy and administer it? | Begin with a verified account purchasing for itself. Bind the HQ owner to a stable identity from a trusted issuer. For team sales, distinguish billing admin, workspace owner and ownership transfer. | Before defining the service contract |
| What can Hosting deliver? | Offer one capacity class and region. Check availability before Checkout. Decide whether to reserve a slot with an expiry or limit sales to invited customers. Handle capacity loss after payment either way. | Before accepting money |
| What does the bill include? | Quote the HQ subscription separately from linked Offices, active employees, hosted execution, managed tokens and third-party licenses. Enable paid extras only after their terms, limits and meters work. Decide currency, taxes, billing start and renewal treatment when setup is delayed. | Before live pricing |
| What happens when service or payment fails? | Assign an operator for paid orders that cannot be fulfilled. Agree a maximum setup delay, notification, credit/refund and renewal cancellation policy. Define grace, restricted access, export and retention after cancellation. | Before paid launch |

## Service ownership and handoff

| Component | Owns |
|---|---|
| Market backend | Customer-facing offers, purchaser and billing account, frozen order terms, payment reconciliation, hosting entitlements, customer status page |
| Hosting web service | Authenticated provisioning API, operation and deployment records, capacity, provider adapters, bootstrap, health and verified workspace address |
| Customer Headquarters | Owner membership, workspace permissions, Office enrollment, model configuration and authority to run agents |

Market and Hosting can run as separate services on the owned PC during validation. Market needs only the Hosting API address and a scoped service credential. Hosting owns infrastructure credentials. A hosted Chief of Staff, if offered later, would need an explicit capacity entitlement and a Headquarters assignment.

| Step | Proposed exchange | Durable outcome |
|---|---|---|
| 1. Quote and checkout | Market resolves plan and price on the server, checks purchaser eligibility, records an order and opens a Checkout session for that order. | The account can resume the existing pending purchase after a double-click or timeout. |
| 2. Confirm payment | Market correlates verified payment evidence to the order, checks amount, currency, paid status and current eligibility. The return page reads order status. | Market commits the hosting entitlement and a pending fulfillment message together. Pending, expired or ineligible payments cannot start deployment. |
| 3. Request a workspace | A Market worker calls Hosting with the order ID as an idempotency key, customer account, owner identity, plan revision, purchased Office quantities by tier, region and entitlement reference. Hosting authenticates Market and verifies the customer and entitlement binding. | Hosting records one operation per purchase. A repeated identical request returns its original operation. A changed payload with the same key produces a conflict. |
| 4. Complete setup | Hosting provisions resources, assigns the owner, tests health and access, then publishes a status hint with the operation revision. | Market reads current authorized status and displays the workspace link when ready. A missed notification can be recovered through bounded discovery of outstanding orders. |

Illustrative contract for step 3: `POST /v1/deployments` returns `202 Accepted`, an operation ID and a status location. `GET /v1/operations/{id}` returns the current state, deployment ID when allocated, and a customer-safe issue code or verified workspace URL. Both operations enforce account scope. The customer signs in to Headquarters through the agreed identity flow. An emailed link or possession of an operation ID conveys no administrator authority.

## Workspace address

Hosting reserves a unique subdomain under `csweet.app` for each Headquarters. It records the hostname against the deployment so retries return the same address and customers can bookmark it through upgrades or host migrations. If a proposed name is taken, Hosting assigns an available name. The public purchase entry remains `www.c-sweet.com`.

Hosting checks HTTPS routing to the correct deployment, application health and owner membership before returning the canonical workspace URL as ready. **My Headquarters** displays that hostname as a direct link while the owner has access, including any active billing grace period. Market reads the address from Hosting's account-scoped deployment record. Headquarters checks the user's identity and membership on arrival. The design preview uses a sample hostname and simulates that handoff locally.

## Subscription lifecycle

Keep **payment status** and **deployment status** visible independently. “Paid / setup delayed” tells support to repair fulfillment. “Past due / running in grace” tells the customer to update payment. Market owns the billing decision and sends Hosting a versioned desired state. Hosting records what it actually applied. Both sides persist changes and their notification records together and reconcile against current authority after missed or reordered events.

Renewal extends access to the same deployment. Period-end cancellation records an effective date. If cancellation or a refund overtakes provisioning, recheck entitlement before exposing the workspace and reconcile any allocated resources. Decide separately when billing stops, when workloads drain, how the owner exports data and when deletion is permitted. Give support a lookup from order to payment, entitlement, operation and deployment, with an audited retry or remedy for each failure.

## Delivery sequence and evidence

1. **Prove the Market flow.** Use Stripe test mode, one recurring hosting offer, one verified owner and a simulated Hosting API. Demonstrate a test purchase, browser close and return, and an account page that distinguishes payment from setup. Verify one order, one entitlement and one provisioning operation after duplicate delivery. Keep forecast revenue and test charges separate.
2. **Exercise real Linux provisioning on TrueNAS.** Run Hosting beside Market on the Mac Studio and clone a Linux guest on the NAS. Demonstrate the buyer opening their assigned Headquarters through Cloudflare and a second account being denied first-run setup. Restart Hosting after resource creation but before acknowledgment. Recover the original deployment and verify that the buyer can still enter it. See [Linux Headquarters deployment](13-linux-headquarters-deployment.md).
3. **Exercise the paid-service lifecycle.** Replay a stale renewal after cancellation, lose the readiness event, exhaust capacity after payment and simulate a failed renewal followed by recovery. Verify customer messages, billing remedies, support lookup and deployment state. An unresolved paid order should have an accountable owner and the customer should see its progress.

The first milestone can start once the offer, buyer identity and request/status contract are agreed. Publishing live prices and enabling payment depend on the commercial and recovery decisions above.
