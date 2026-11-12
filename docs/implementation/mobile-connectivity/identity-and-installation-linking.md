# C-Sweet accounts and linking a Headquarters at install

Date: 2026-11-11. **Proposed design; nothing here is implemented.** Decision D3 in the [decision log](decisions.md): shared sign-in is in scope now, the choice between C-Sweet services and private hosting is made at initial install, and choosing C-Sweet services creates the administrator's C-Sweet Market account during registration. This guide replaces issue 3 of [Phase 2](phase-2-accounts.md) and changes the Identity and Market rows in [architecture.md](architecture.md).

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

A **C-Sweet account is a Market account**. Market becomes the OpenID Connect issuer for the Market site, the phone app and linked Headquarters installations. Because the account is the same record, existing Market customers keep their ID, roles and purchases with no linking step. On first run Headquarters asks one question: **Connect to C-Sweet** or **Private Headquarters**. Connecting creates (or signs in to) the C-Sweet account, registers the installation with C-Sweet, and turns on the official relay, directory and push. Private keeps everything on the operator's network and behaves as Headquarters does today.

This replaces the earlier proposal of a separate `CSweet.Identity` repository. Trade-off: a Market outage becomes a sign-in outage for new sessions. To keep the option of moving the issuer later, publish it on its own hostname (an account hostname, not the marketplace hostname) from the first release, so clients never need to change.

## First-run flow at Headquarters

```text
CHOOSE                          CONNECT TO C-SWEET              PRIVATE
┌───────────────────────────┐   ┌───────────────────────────┐   ┌───────────────────────────┐
│ How will you run C-Sweet? │   │ 1 Your administrator      │   │ Your administrator        │
│                           │   │   Name, local password    │   │ Name, email, password     │
│ ◉ Connect to C-Sweet      │   │ 2 [Continue with C-Sweet] │   │ [Create administrator]    │
│   Phone app, marketplace  │   │   or open account.…/device│   │                           │
│   purchases and sign-in   │   │   and enter  WDJB-MJHT    │   │ Phone access can be added │
│   work out of the box.    │   │   [QR]  Waiting…          │   │ later with your own relay.│
│ ○ Private Headquarters    │   │ 3 Recovery codes          │   │                           │
│   Nothing leaves your     │   │                           │   │                           │
│   network.                │   │ [Use private instead]     │   │                           │
│ [Continue]                │   │                           │   │                           │
└───────────────────────────┘   └───────────────────────────┘   └───────────────────────────┘
```

1. `/register` gains a first step, the hosting choice. Nothing is contacted until the person presses Continue on **Connect to C-Sweet**.
2. **Local administrator.** Name and a local password, as today. The local password remains the way to sign in to Headquarters in a browser on the local network, and the way back in if C-Sweet is unreachable.
3. **C-Sweet account, by device authorization** (RFC 8628). Headquarters asks the issuer for a device code and shows a **Continue with C-Sweet** button and a QR code. Both open the C-Sweet site with the code already filled in, so nothing is typed; the short code is also shown as a fallback for opening the site by hand on another device. The person signs in or creates the account there (email confirmation happens on the C-Sweet site), and approves "Link this Headquarters". Headquarters polls and receives an ID token and an installation-scoped token.

   Why this flow: Headquarters is often reached at a LAN address or a machine name, so no redirect URI can be registered in advance, and the password for the C-Sweet account must never be typed into Headquarters.
4. Headquarters validates the ID token (issuer, audience, signature, nonce, expiry), then in one transaction creates the initial administrator, an `ExternalIdentityLink` (issuer + subject), the installation link record and the recovery codes.
5. Headquarters generates an installation key pair, sends the public key with the installation-scoped token to register the installation, and stores the returned installation ID.
6. Mobile access is set to **Official** and the relay and push enrollment job is queued; Headquarters presents its installation token to ConnectionService for this. Setup continues to `/setup` as today. If relay enrollment fails, setup is not blocked; Settings → Mobile access shows the state.

Failure handling: the device code expires after 15 minutes and the screen offers a new one. **Use private instead** is available throughout. If the person closes the browser after step 4 committed, reopening continues from step 5. If the account's email is not yet confirmed, the C-Sweet site completes confirmation before approving the link.

**Private.** The current registration form, unchanged. No request is made to any C-Sweet host. Mobile access starts Disabled.

**Existing installations.** After upgrade the mode is Private and nothing changes. Settings → C-Sweet account offers **Connect to C-Sweet**, which runs steps 3–6 for the signed-in administrator.

**Unlinking.** Settings → C-Sweet account → Disconnect asks for the local password, stops directory publication and official relay use, and tells C-Sweet to retire the installation. Local data, local accounts and private relay settings are untouched. Members who only had a C-Sweet sign-in need a local password or a company identity before they can sign in again; the confirmation lists them.

## Members and the phone

- An administrator invites a person by email ([multi-user guide](multi-user-headquarters.md)). On a linked installation the invitation requires the C-Sweet issuer; the directory shows it to that account after sign-in.
- The phone signs in with authorization code + PKCE in the system browser. Scheme and app-link registration are part of issue IL-07.
- A member's C-Sweet account is linked to their Headquarters person by issuer + subject when they accept the invitation. Email is never used to match.
- Headquarters web sign-in for members on a linked installation: local password set at acceptance in v1. Signing in to the Headquarters web UI with the C-Sweet account is an open question (IL-Q1).

## Data model

**Headquarters** (`CSweetDbContext`)

| Table | Columns |
| --- | --- |
| `InstallationLink` (single row) | `Mode` (Private, Linked), `Issuer`, `InstallationId`, `InstallationKeyReference` (protected storage, never the key), `LinkedByApplicationUserId`, `LinkedAt`, `State` (PendingRegistration, Active, Retiring, Retired), `Revision` |
| `ExternalIdentityLinks` | Defined in the multi-user guide; shared with it. |
| `DeviceAuthorizationAttempts` | `Id`, `DeviceCodeProtected`, `UserCode`, `ExpiresAt`, `Purpose` (InitialRegistration, LinkExisting), `Status`; pruned after 24 hours |

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
| Headquarters | `GET /api/installation/hosting` (anonymous while registration is open) | Current mode and whether C-Sweet is reachable. |
| Headquarters | `POST /api/auth/register/linked/start` | Validate the local administrator fields, start device authorization, return user code and verification link. |
| Headquarters | `POST /api/auth/register/linked/poll` | Poll; on success perform step 4 and sign the administrator in. |
| Headquarters | `POST /api/installation/link/start`, `…/poll`, `DELETE /api/installation/link` | Link or unlink an existing installation. Administrator only. |
| Market | Standard discovery, authorization, token, device authorization, user-info and key endpoints | Issuer. |
| Market | `/device` page | Enter the user code, sign in or register, approve the named Headquarters. |
| Market | `POST /api/installations`, `DELETE /api/installations/{id}` | Register or retire. Requires the installation-scoped token; register proves possession of the installation key. |
| Market | `PUT /api/installations/{id}/members/{subject}`, `GET /api/me/enterprises` | Directory publication (signed by the installation key) and self lookup. |

## Issues

### Market repository

**IL-01 · Send real Identity email — S · Junior + review.** Replace `IdentityNoOpEmailSender` with an adapter over the existing SMTP sender; remove the no-op branch in `RegisterConfirmation.razor`. Done when a new account receives a confirmation email in the test SMTP sink and cannot sign in before confirming.

**IL-02 · Spike: issuer library and device flow — M · Senior-led.** Add an OpenID Connect server library to a throwaway copy, confirm authorization code + PKCE and device authorization both work against the existing Identity store, and that existing cookie sign-in to the Market site is unaffected. Record the library and version in the decision log.

**IL-03 · Issuer in Market — L · Senior-led.** Server tables and migration, signing keys with rotation, discovery on the account hostname, the three client registrations, consent screen. Tests: wrong redirect, wrong PKCE verifier, replayed code, expired device code, unconfirmed email.

**IL-04 · Device approval page — M · Junior + review.** `/device`: enter code, sign in or register, show "Link Headquarters *name* to your account?", approve or deny. Rate limit code entry. Tests for unknown, expired and already-used codes.

**IL-05 · Installation registration API — M · Senior-led.** `Installations` table, register with proof of key possession, retire. One account may own several installations.

**IL-06 · Directory API — M · Senior-led.** `InstallationMembers`, publication authenticated by the installation key with revision checks (an older revision never overwrites a newer one; Removed is a tombstone), self lookup only, no lookup by email.

### C-Sweet repository

**IL-07 · Phone sign-in launcher — M · Senior-led.** Implement `IMobileSignInLauncher.LaunchSharedSignInAsync` from P1-05 with the system browser, PKCE, state and nonce; register the callback scheme and app links on both platforms.

**IL-08 · Hosting choice step — M · Junior + review.** Add the choice as the first step of `Register.razor` with the three layouts above; add `GET /api/installation/hosting`. Private path must make no outbound request (assert with a handler that fails the test on any call). All strings localized.

**IL-09 · Linked registration — L · Senior-led, juniors take the UI.** `InstallationLink` and `DeviceAuthorizationAttempts`, start and poll endpoints, ID-token validation, the single transaction in step 4, resume after a crash between steps 4 and 5. Tests: expired code, denied approval, token for the wrong audience, second concurrent poll, C-Sweet unreachable (form offers Private).

**IL-10 · Installation key and registration — M · Senior-led.** Key generated and stored through the same protected storage chosen in the M0 spike; registration retried from an outbox.

**IL-11 · Settings → C-Sweet account — M · Junior + review.** Shows mode, linked account email, installation state; Connect and Disconnect with the confirmations described above.

**IL-12 · Directory publication from Headquarters — M · Senior-led.** Outbox entry on every invitation and membership change; bounded reconciliation after an outage; nothing published in Private mode.

**IL-13 · Upgrade test — S · Junior.** An existing database upgrades to Private with no outbound calls and unchanged sign-in.

## Open questions

- **IL-Q1.** Should members be able to sign in to the Headquarters web UI with their C-Sweet account? It needs either the device flow on every sign-in or a registered redirect, which only works when the browser is on the Headquarters machine.
- **IL-Q2.** Account deletion at C-Sweet: what happens to installations the account owns? Apple requires in-app account deletion once the app can create accounts.
- **IL-Q3.** Is the Market site's own sign-in moved onto the issuer in the same release, or later?
