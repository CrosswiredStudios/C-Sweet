# C-Sweet accounts and linking a Headquarters at install

Proposed implementation for D3, updated 2026-11-12. Start with the four [hosted onboarding deliveries](hosted-onboarding.md). This guide specifies the shared account, installation link and directory used by the initial owner and later members.

## What exists today

| Fact | Source |
| --- | --- |
| Market uses ASP.NET Core Identity with cookie sign-in, PostgreSQL, registration, email confirmation, password reset, two-factor and passkeys pages. `RequireConfirmedAccount` is true. | `CSweet.Market.Web/Program.cs`, `Components/Account/Pages` |
| Identity emails go to `IdentityNoOpEmailSender`, so confirmation and reset emails are never sent. A real SMTP sender exists for marketplace notifications (`SmtpMarketplaceEmailSender`). | `CSweet.Market.Web/Program.cs` lines 62 and 90 |
| Market is not an OpenID Connect issuer. It has no token or authorization endpoints. | `Program.cs`, package list |
| Market `ApplicationUser` has string IDs and no extra fields. | `Data/ApplicationUser.cs` |
| Headquarters registers its administrator before the setup wizard. `Routes.GuardNavigationAsync` sends an unregistered installation to `/register`, then to `/setup`. | `src/CSweet.UI/Routes.razor` |
| Headquarters already knows a Market base URL. | `Marketplace` section of `src/CSweet.Api/appsettings.json` |

## Design in one paragraph

A C-Sweet account is a Market account. The live Marketplace handles registration, login, verification and account recovery. Production Market configures `https://c-sweet.com` as its public origin (`CSweet.Market/compose.production.yml`, `Notifications__Smtp__PublicBaseUrl`). Configure the trusted issuer explicitly for this deployment. HQ's catalog URL serves a different purpose. Existing Market customers retain their ID, roles and purchases.

**Issuer address (D7).** The OpenID Connect issuer is a dedicated account hostname, proposed `https://account.c-sweet.com`, served by the Marketplace application alongside `https://c-sweet.com`. Discovery, authorization, token, device and `/device` approval pages live on the account hostname; Headquarters and the phone app are configured with that issuer only. Market's cookie sign-in for its own site is unchanged.

## Private installations

A private Headquarters never contacts the account hostname. Headquarters itself is the account authority, using the accounts it already stores (`ApplicationUser` in its own database), and it does **not** run an OpenID Connect issuer:

| Need | How a private installation does it |
| --- | --- |
| Administrator account | Today's local registration and recovery codes (unchanged by H1). |
| More people | Member invitations from the [multi-user guide](multi-user-headquarters.md), delivered by the administrator through an internal channel (paste, internal email, QR). The invitee sets a local password while redeeming it. |
| Phone sign-in | QR or pasted pairing invitation from Headquarters. Inside the pinned encrypted channel the person activates or proves their local account once; after that the phone's device key is the credential, bound to that person. No browser redirect, no issuer. |
| Lost password or phone | Local administrator reset and device revocation; root recovery codes for the administrator. |
| Company directory (optional) | Headquarters trusts the company's own OpenID Connect provider on the internal network (decision D9, not yet scheduled). |

**Air-gapped networks.** Everything above works without internet access when the phone reaches an internal relay over the company network or VPN, with internal DNS and a locally trusted TLS certificate. What does not work: push notifications (Apple and Google delivery needs the internet; the app relies on foreground updates instead), and anything that depends on C-Sweet online services, such as Marketplace agent and plugin installs, which need their own offline import path outside this plan.

**Why Headquarters is not an issuer.** Only one client, the phone, needs to authenticate to a private Headquarters, and it already has a stronger credential (an enrolled device key checked on every connection). Adding an issuer would bring signing-key management, redirect handling on internal hostnames and a second login protocol to every private install for no gain. If a later need appears (other internal apps signing in with Headquarters accounts), the IL-02 spike shows the same library would serve.

## First-run flow at Headquarters

1. A fresh HQ opens `/register` at **Hosted** or **Private**. Hosted explains that C-Sweet provides sign-in, discovery and relay service while HQ runs on this machine. Displaying this choice reads only local state.
2. Hosted offers **Create account** and **Sign in**. Generate a protected installation key and an expiring device-authorization attempt bound to that key and the initiating setup browser. Both actions open the real Market account site with the appropriate entry intent. Preserve the pending approval during registration, email verification, login and a returning user's existing Market session.
3. At Market the user compares the code displayed by HQ, reviews the installation name and signed-in account, and approves or denies the link. Offer the complete verification URL or QR plus manual code entry. Only Market receives the Market password. HQ polls at the issuer's prescribed interval and handles pending, slow-down, denial and expiry.
4. On the initiating HQ, validate the issuer, signature, audience, subject, nonce and expiry and show the approved Market identity. Set a separately labeled local recovery password. Atomically create the initial administrator, external identity link, recovery codes, pending installation link and registration outbox record. Deliver recovery codes through the existing save-and-continue screen.
5. Register at Market with proof of possession of the approved installation key and an installation-scoped grant. Retry a lost response with the same operation identity and key. Market returns the same installation ID. For an existing HQ, attach the identity to the signed-in administrator and preserve the existing local credentials and recovery codes.
6. Set official mobile enrollment pending and continue the HQ setup wizard. When setup commits complete, persist an owner-directory publication request in that transaction. Publish after Market registration completes using the stable Market owner, installation ID, display name and revision. Relay readiness is reported separately. An offline connector leaves the completed instance discoverable.

Before local registration commits, cancellation or **Use Private** invalidates the pending attempt. After commit, reopen and resume the pending operation. Recheck registration eligibility on commit so competing private and hosted setup attempts cannot create two administrators. On a lost remote acknowledgement reconcile by installation key. A completed HQ can replay its directory publication safely after restart.

**Private.** The current registration form, unchanged. No request is made to any C-Sweet host. Mobile access starts Disabled.

**Existing installations.** Upgrade retains private mode and the current login. An administrator can later choose Hosted in Settings and approve a link through the same Market flow.

**Unlinking.** Settings → C-Sweet account → Disconnect asks for the local password, stops directory publication and official relay use, and tells C-Sweet to retire the installation. Local data, local accounts and private relay settings are untouched. Members who only had a C-Sweet sign-in need a local password or a company identity before they can sign in again; the confirmation lists them.

## Members and the phone

- The initial owner appears through setup completion and needs no member invitation. Resolve phone and HQ subjects to the same Market account inside the issuer/directory, including when clients receive different pairwise subjects. Return installations for the authenticated caller from `GET /api/me/enterprises`. The phone shows **My Headquarters** with a separate enrollment and connection state for each installation.
- Selecting an instance obtains a short-lived route ticket scoped to its installation and phone key. HQ verifies the device, current identity and enrollment policy before serving business data.
- An administrator may subsequently invite additional people through the [multi-user guide](multi-user-headquarters.md).
- The phone signs in with authorization code + PKCE in the system browser. Scheme and app-link registration are part of issue IL-07.
- A member's C-Sweet account is linked to their Headquarters person by issuer + subject when they accept the invitation. Email is never used to match.
- Headquarters web sign-in for members on a linked installation: local password set at acceptance in v1. Signing in to the Headquarters web UI with the C-Sweet account is an open question (IL-Q1).

## Data model

**Headquarters** (`CSweetDbContext`)

| Table | Columns |
| --- | --- |
| `InstallationLink` (single row) | `Mode` (Private, Linked), `Issuer`, `InstallationId`, `InstallationKeyReference` (protected storage, never the key), `LinkedByApplicationUserId`, `LinkedAt`, `State` (PendingRegistration, Active, Retiring, Retired), `Revision` |
| `ExternalIdentityLinks` | Defined in the multi-user guide; shared with it. |
| `DeviceAuthorizationAttempts` | `Id`, setup-session binding, installation key reference, protected device code, nonce, user code, expiry, purpose and status. Prune terminal attempts after 24 hours. |

**Market**

| Table | Columns |
| --- | --- |
| OpenID Connect server tables | Applications, authorizations, scopes, tokens (from the server library) |
| `Installations` | `Id`, `OwnerUserId`, `PublicKeySpki`, `DisplayName`, `CreatedAt`, `RetiredAt`, `Revision` |
| `InstallationMembers` (the directory) | `InstallationId`, `UserId`, `State` (Invited, Active, Removed), `Descriptor` (enterprise name, relay binding, Headquarters key pin), `Revision` |

Registered clients: Market web (confidential), phone app (public, PKCE, custom scheme and app links), Headquarters (public, device flow only).

## Endpoints

| Where | Route | Purpose |
| --- | --- | --- |
| Headquarters | `GET /api/auth/registration-options` (anonymous; implemented 2026-11-13) | Returns `RegistrationOpen` and `HostedSetupAvailable` from local configuration (`Installation:LinkingEnabled`) only. Under `/api/auth`, which the first-run guard already allows. |
| Headquarters | `POST /api/auth/hosted/start` | Start a key-bound attempt for the initiating browser and selected Market account action. |
| Headquarters | `POST /api/auth/hosted/poll`, then `…/complete` (and `…/cancel`) | Return approval state to the initiating browser. On explicit local completion commit step 4 once and sign the administrator into HQ. |
| Headquarters | `POST /api/installation/link/start`, `…/poll`, `DELETE /api/installation/link` | Link or unlink an existing installation. Administrator only. |
| Market | Standard discovery, authorization, token, device authorization, user-info and key endpoints | Issuer. |
| Market | `/device` page | Enter the user code, sign in or register, approve the named Headquarters. |
| Market | `POST /api/installations`, `DELETE /api/installations/{id}` | Register or retire. Requires the installation-scoped token; register proves possession of the installation key. |
| Market | `PUT /api/installations/{id}/members/{subject}`, `GET /api/me/enterprises` | Directory publication (signed by the installation key) and self lookup. |

## Issues

### Market repository

**IL-01 · Verify mailbox ownership at Market — Senior-led migration, junior-owned email adapter.** Replace the no-op sender and confirmation-page shortcut. Prove real delivery for new accounts. Reverify legacy mailboxes of uncertain provenance before trusting their email for hosted linking. Preserve the existing Market account and entitlements.

**IL-02 · Spike: issuer library and device flow — M · Senior-led.** Add an OpenID Connect server library to a throwaway copy, confirm authorization code + PKCE and device authorization both work against the existing Identity store, and that existing cookie sign-in to the Market site is unaffected. Record the library and version in the decision log.

**IL-03 · Issuer in Market — L · Senior-led.** Server tables and migration, signing keys with rotation, discovery on the account hostname, the three client registrations, consent screen. Tests: wrong redirect, wrong PKCE verifier, replayed code, expired device code, unconfirmed email.

**IL-04 · Approve the installation — Senior-led protocol, junior-owned UI.** Resume the same pending approval after either Market registration or existing-account login. Display the account, installation and matching code. Test denial, expiry, switched account, incompatible key and replay.

**IL-05 · Installation registration API — M · Senior-led.** `Installations` table, register with proof of key possession, retire. One account may own several installations.

**IL-06 · Directory API — M · Senior-led.** `InstallationMembers`, publication authenticated by the installation key with revision checks (an older revision never overwrites a newer one; Removed is a tombstone), self lookup only, no lookup by email.

### C-Sweet repository

**IL-07 · Phone sign-in launcher — M · Senior-led.** Implement `IMobileSignInLauncher.LaunchSharedSignInAsync` from P1-05 with the system browser, PKCE, state and nonce; register the callback scheme and app links on both platforms.

**IL-08 · Hosting choice — Junior + review.** Implement H1 in `Register.razor`, the local bootstrap read and its first-run guard test. Cover Hosted, Private, Back, refresh, existing installations and zero external onboarding calls before opt-in.

**IL-09 · Linked registration — L · Senior-led, juniors take the UI.** `InstallationLink` and `DeviceAuthorizationAttempts`, start and poll endpoints, ID-token validation, the single transaction in step 4, resume after a crash between steps 4 and 5. Tests: expired code, denied approval, token for the wrong audience, second concurrent poll, C-Sweet unreachable (form offers Private).

**IL-10 · Installation key and registration — Senior-led.** Persist the host key before device authorization. Bind Market approval to it and retry registration from the outbox. Prove one installation after a lost acknowledgement. The phone TLS spike is a dependency for H4 transport.

**IL-11 · Settings → C-Sweet account — M · Junior + review.** Shows mode, linked account email, installation state; Connect and Disconnect with the confirmations described above.

**IL-12 · Directory publication — Senior-led.** Project the initial owner on completed hosted setup, then member invitations, access changes, unlink and removals. Use revisioned outbox publication and bounded reconciliation. Cover completed-before-registration and offline relay. Private installations publish nothing.

**IL-13 · Upgrade test — S · Junior.** An existing database upgrades to Private with no outbound calls and unchanged sign-in.

## Open questions

- **IL-Q1.** Should members be able to sign in to the Headquarters web UI with their C-Sweet account? It needs either the device flow on every sign-in or a registered redirect, which only works when the browser is on the Headquarters machine.
- **IL-Q2.** Account deletion at C-Sweet: what happens to installations the account owns? Apple requires in-app account deletion once the app can create accounts.
- **IL-Q3.** Is the Market site's own sign-in moved onto the issuer in the same release, or later?
