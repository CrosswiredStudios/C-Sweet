# Mobile plan decision log

One row per decision. Change a decision by adding a dated note, not by rewriting history.

| # | Decision | Status | Outcome |
| --- | --- | --- | --- |
| D1 | Who can use Headquarters and the app | **Decided 2026-11-11** | Headquarters becomes multi-user now. Design and issues: [multi-user-headquarters.md](multi-user-headquarters.md). |
| D2 | How phone pages reach Headquarters | **Revised again 2026-11-12** | **Thin shell is the target**: each enterprise's pages come from its own Headquarters, to avoid permanent version skew across about 660 routes. Redesigned after review (no loopback proxy; intercepted GETs at a per-enterprise origin, API calls through the bridge, per-enterprise data stores, contained bridge). A prototype (TS-13) runs alongside H1–H3 and decides how H4 is built. Bundled screens over the same tunnel are the fallback. Option B stays dropped. See [tunnel guide](tunnel-and-thin-shell.md). |
| D3 | Shared sign-in and Market | **Decided 2026-11-11** | In scope now. Hosting choice (C-Sweet or private) is made at initial install. Choosing C-Sweet creates the administrator's C-Sweet Market account during registration. Design: [identity-and-installation-linking.md](identity-and-installation-linking.md). |
| D4 | Release bar | Open | With the tunnel, full parity is reachable; the order within Phases 5–6 still needs to be set (approvals, messages, overview and boards first is the recommendation). |
| D5 | Push notifications | **Decided 2026-11-11** | Part of the first release. Design: [push-notifications.md](push-notifications.md). |
| D6 | One numbering scheme | **Applied 2026-11-12** | H1–H4, then Phases 2–7. M0–M6 and increment numbers are retired labels; crosswalk in [increments.md](increments.md). |

| D7 | Where the sign-in provider lives | **Decided 2026-11-13** | A dedicated account hostname, proposed `https://account.c-sweet.com`, served by the Marketplace application. Installations and phones trust only this issuer address, so the issuer can later move off the Marketplace without re-linking anyone. The Marketplace site itself stays at `https://c-sweet.com`. Exact hostname to be confirmed before IL-03 ships. Private installations do not use it (see the identity guide, "Private installations"). |
| D8 | Contributor approvals | **Open** | See MU-Q1 in the multi-user guide. |
| D9 | Company sign-in for private installs | **Open** | Has a release acceptance requirement but no issues. Assign to a release or break it down. |
| D10 | Who may supply executable UI to the published app | **Open** | Affects the thin-shell threat model and store review. The design assumes any Headquarters may be hostile, since Headquarters is open source and self-hosted. |

**IL-02 · Issuer library · 2026-11-13.** **OpenIddict 7.8.0** (Apache-2.0; `OpenIddict.AspNetCore`, `OpenIddict.EntityFrameworkCore`) on .NET 10 over the existing ASP.NET Core Identity store. The [spike](spikes/il-02-openiddict/README.md) passed six end-to-end tests: discovery advertises the device endpoint and S256; Headquarters device flow returns `authorization_pending`, then an ID token for the approving Market user after approval; the device code is single use; a denied approval returns `access_denied`; an unknown user code is rejected; phone code + PKCE rejects a wrong verifier and returns the right nonce; an unregistered redirect URI is refused; ordinary Identity cookie sign-in keeps working beside the issuer. Not covered, to do in IL-03: persistent signing and encryption keys with rotation, `slow_down` and expiry behaviour, the consent page in Market's real UI and login redirect, rate limits on code entry, token lifetimes.

**Independent review · 2026-11-12.** A second model reviewed the plan against source. Its blocking findings were applied to the multi-user, tunnel and push guides on the same day: resource ownership checks, dispatcher entry proof, minimal realtime hints instead of hub forwarding, one write-recovery contract, a push gateway that does not trust the relay, wider restore, and corrected invitation states.

**D3 clarification · 2026-11-12.** First deliver the Hosted/Private chooser. Hosted supports both new registration and existing-account sign-in at the live C-Sweet Marketplace. After setup, signing in to the phone app with that account lists the owner's HQ instances and allows selecting one for device enrollment and connection. Build and accept [H1 through H4](hosted-onboarding.md) in that order.

Design choices recorded for D3 and D5 follow.

- The C-Sweet account **is** the Market account and Market hosts the issuer, instead of a new `CSweet.Identity` repository. This removes the account-linking workflow entirely.
- Headquarters links by **device authorization**, because Headquarters has no fixed address to redirect back to. **Confirmed 2026-11-11**, with the code carried inside a one-click link and QR code so typing is only a fallback. Using ConnectionService as a sign-in broker was considered and not taken: it would still need polling, a new pre-enrollment protocol and a second service in the sign-in path. ConnectionService is used immediately afterwards for relay and push enrollment.
- The administrator still sets a **local password** on a linked installation, so the local network sign-in keeps working when C-Sweet is unreachable.
- Push goes through a **C-Sweet push gateway**; isolated private networks cannot have push.

## D2 · How phone pages reach Headquarters

### The situation

The phone app ships the same pages as the web app (`CSweet.UI`). Those pages get their data by making HTTP calls: 26 typed API clients and 84 pages that use `HttpClient` directly, against about 660 API routes. On the phone there is no direct network path to Headquarters. There is only the encrypted channel through the relay. Something has to carry each page's request across that channel and run it at Headquarters.

### Option A · Allowlisted HTTP tunnel

The pages stay exactly as they are. On the phone, the `HttpClient` they already use is given a different bottom layer: instead of opening a network connection, it writes the request (method, path, body, a few permitted headers) into the encrypted channel. At Headquarters a dispatcher reads it, checks the route against an allowlist, sets the caller to the person who owns the enrolled device, and runs it through the same API pipeline the web uses. The response travels back the same way.

- "Allowlisted" means each route must be explicitly marked as available to phones, in code, next to its mapping. An unmarked route does not exist as far as the phone is concerned. The mark also states whether the route is a read or a write.
- No cookies and no antiforgery token are involved. Identity comes from the device's TLS session, never from anything in the request.
- Writes carry an operation key. Important writes (messages, decisions, creating things) pass that key into their service so a retry returns the first result. Other writes get a generic receipt: if the response is lost the app says "check the result" instead of resending.
- Large uploads and downloads use the separate file-transfer lane already in the plan, not the tunnel.

### Option B · Purpose-built operation catalog (what the architecture guide currently says)

The phone does not send HTTP. For each thing a person can do there is a named, versioned operation, for example `conversation.sendMessage v1`, with its own request and response types. Headquarters has one adapter per operation that calls the application service directly. Pages on the phone are changed to call a new `IMobileHeadquartersClient` instead of `HttpClient`.

### Option C · Thin shell that loads the web app through the tunnel

The app contains no pages. It runs a small local proxy and its WebView loads the Headquarters web app through the encrypted channel, like a browser on the local network would.

### Comparison

| | A · HTTP tunnel | B · Operation catalog | C · Thin shell |
| --- | --- | --- | --- |
| Work to build the mechanism | Medium, once | Medium, once | Small, once |
| Work per workflow (187 in the matrix) | Mark the route, add a test. Tier-1 writes also take the operation key. | New contract, adapter, client method, and a changed page | None |
| Existing pages | Unchanged | Every data call rewritten, or a second set of pages | Unchanged |
| New web features on the phone | Mark the route | Build the operation | Automatic |
| Where authorization runs | The same endpoint pipeline as the web, including the new multi-user checks | Wherever each adapter calls; must be re-proved per adapter | Same as web |
| What a compromised or buggy phone can reach | Any marked route, with that person's rights | Only the listed operations | Everything the web can reach |
| Duplicate-safe writes | Strong for tier-1 writes, "check the result" for the rest | Strong for every operation | Weak; it is a browser |
| App and Headquarters on different versions | Pages may call a route that changed. Needs a published supported-version window and an "update needed" screen. | Operations are versioned individually; most tolerant | No skew; pages always match Headquarters |
| Data over the relay | Requests and responses only | Smallest | Also downloads the whole web app, repeatedly; counts against the traffic allowance |
| Fit with rules already in the plan | Needs the "no HTTP" rule replaced by "only marked routes" | Fits as written | Breaks "no cookie emulation" and "phone packages its own shell" |
| Time to full parity | Months | Much longer than the rest of the plan combined | Weeks, with the weakest safety |

### How D1 changes the picture

The multi-user design enforces access in the API pipeline: an endpoint filter plus a test that fails when any endpoint lacks an access rule. Under A, phone requests pass through that same filter, so one piece of enforcement covers web and phone. Under B, adapters call services directly and skip the filter, so the access rules would have to be moved down into every service, or duplicated in every adapter. That is a large amount of extra security-critical work, and a place for the two paths to drift apart.

### Outcome (2026-11-11)

After discussion, two cells in the comparison above were corrected: the allowlist and device-based identity can sit in front of a thin shell too, so C does not have to reach everything or emulate cookies. C is option A's tunnel with the pages served by Headquarters. It also suits one phone connected to several enterprises on different Headquarters versions, which bundled pages cannot match. Its open risks are speed and memory of the web app in a phone browser view, the local proxy, keeping enterprises' browser data apart, running Headquarters-served code inside the app, and app-store review. The owner chose to build the shared tunnel and trial C; see the [tunnel and thin shell guide](tunnel-and-thin-shell.md).

### Original recommendation (superseded)

A. It is the only option where full parity is realistic, and it keeps one authorization path. Keep the spirit of B for the small set of writes where a duplicate would hurt (messages, decisions, creating records, anything that spends money or starts work): those take the operation key into the domain service exactly as the architecture guide describes.

What A costs you: the allowlist becomes a security boundary that must be reviewed on every pull request, the app and Headquarters need a stated compatibility window, and a route that was only ever safe because a single trusted administrator called it must be fixed before it is marked. The multi-user work finds exactly those routes.

Choose B if you want the phone to do a short, fixed list of things (approvals, messages, overview) and are willing to drop full parity as a goal. Do not choose C.
