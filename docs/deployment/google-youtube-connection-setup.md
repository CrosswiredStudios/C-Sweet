# Register C-Sweet with Google

Registration instructions checked: 2026-10-28. For the C-Sweet owner/operator, not ordinary customers.

You register our application. Customers later click **Connect account** and approve access on Google.
Google needs our application name, the access we request, and an exact address to return to after
sign-in. This is the standard Google app registration, not a separate setup task for each customer.
The [managed connection/import path](../implementation/managed-connection-service-progress.md) is
implemented, but general managed actions, deployment and real-provider acceptance are unfinished.
The proposed managed callback is `https://connections.c-sweet.com/oauth/callback/com.csweet.google.youtube`;
that service is not deployed yet. Use the local steps below only for an authorized development test;
do not distribute our shared client secret to customer installations. The service repository's
`docs/hosting-mac-studio.md` describes the Mac Studio/Cloudflare deployment and central credential import.

## Quick start for your first real connection

1. Open [Google Cloud Console](https://console.cloud.google.com/) and create **C-Sweet YouTube Development**.
2. In **APIs & Services → Library**, enable **YouTube Data API v3** and **YouTube Analytics API**.
3. Open **Google Auth platform → Branding → Get started**. Enter **C-Sweet**, a support email and a
   developer contact email. Choose **External** for the audience.
4. Keep the application in **Testing**. In **Audience → Test users**, add the Google account that
   owns your test channel.
5. In **Data Access → Add or remove scopes**, select `openid`, `profile`,
   `https://www.googleapis.com/auth/youtube.readonly` and
   `https://www.googleapis.com/auth/yt-analytics.readonly`. Do not add publishing access yet.
6. In **Clients → Create client**, choose **Web application**. Name it **C-Sweet Local Development**.
   Add the exact callback address for the API you are running, as explained below. You do not need
   to add JavaScript origins for this server-side authorization flow.
7. Save the **client ID** and **client secret** securely. In C-Sweet, open **Settings → Plugins →
   Installed → OAuth provider profiles → Google → Configure declared provider**. Enter those two
   values and save. The reviewed plugin supplies the endpoints. Never paste the secret into chat or Git.
8. Return to the installed plugin, refresh its status, and click **Connect account**. This opens
   Google's actual sign-in and permission page, not an in-app imitation. After consent, verify the
   imported channel and explicitly choose business access.

These console steps follow [Google's consent-screen instructions](https://developers.google.com/workspace/guides/configure-oauth-consent)
and [web-client registration instructions](https://developers.google.com/identity/protocols/oauth2/web-server).

### Use the correct development callback

The callback goes to the **API**, not the browser app at `http://localhost:5097`. For example, when
running `CSweet.Api` with its standard `https` launch profile on port 7033, register:

```text
https://localhost:7033/api/plugin-connections/oauth/callback
```

Configure these non-secret values for that API deployment, then restart it:

```text
CSweet:PluginConnections:PublicBaseUrl = https://localhost:7033
CSweet:PluginConnections:ReturnBaseUrl = http://localhost:5097
```

`PublicBaseUrl` is the API address Google calls. `ReturnBaseUrl` is where C-Sweet takes you afterward.
HTTP is permitted for the UI return only on loopback in Development; the callback remains HTTPS.
Use your actual API HTTPS port if Aspire assigns a different endpoint, or configure a stable endpoint
first. Google and C-Sweet must use the identical callback. Trust the local development certificate
and confirm you can reach that API before testing. When configured, **Plugin settings → Application
redirect URI** shows the exact address to copy into Google.

Use the API project's normal deployment configuration or user secrets for these values, not the
WebAssembly app's public configuration. This is a local test setup, not the future shared service.
Before customer launch, complete the production registration, service deployment and verification
steps below.

## Before you start

Decide the domain we will own and operate for connections. Throughout this checklist,
`YOUR-DOMAIN` means that real domain, not a value to copy literally.

Prepare a monitored support email, developer contact email, C-Sweet logo, public product homepage,
privacy policy and terms. The policies must explain the managed service, customer-hosted instances,
Google/YouTube access, sharing with permitted businesses/agents and processors, retention and deletion.
Do not describe the service as already Google-verified.

## 1 Create a development project

1. Open [Google Cloud Console](https://console.cloud.google.com/).
2. Sign in with the account that will administer C-Sweet's Google integration.
3. Open the project selector at the top and choose **New project**.
4. Name it **C-Sweet YouTube Development**. Record its project ID and number.
5. Select this project before making the changes below.

Use a separate **C-Sweet YouTube Production** project for real customers, and a separate staging tier
if used. Keep local/test callback addresses out of the production client. These are environment
boundaries, not customer-by-customer quota pools.
[Google environment setup](https://developers.google.com/identity/protocols/oauth2/production-readiness/policy-compliance).

Use company-controlled project ownership, MFA, least-privilege IAM and a recovery owner. Do not give
the deployed service broad project-owner permissions merely to read its assigned secrets.

## 2 Enable the YouTube APIs

Open **APIs & Services → Library**, then enable:

- **YouTube Data API v3** for channels, videos and interactions.
- **YouTube Analytics API** for the reporting feature.

Do not enable unrelated APIs or request partner privileges just because they may be useful later.
Ordinary channel access uses user OAuth, not a service account acting as the channel owner.
[YouTube authorization setup](https://developers.google.com/youtube/data/guides/auth/server-side-web-apps).

## 3 Set up the consent screen

Open **Google Auth platform → Branding**. If prompted, choose **Get started**.

Enter **C-Sweet** as the application name, the support email, and developer contact email. Under
**Audience**, choose **External**, because customers are outside our own Google Workspace organization.
Initially keep development in **Testing** and add your authorized test users under Audience.

Customers will see this application identity when Google asks them to approve access.
[Google consent-screen instructions](https://developers.google.com/workspace/guides/configure-oauth-consent).

## 4 Add the website and policy links

In Branding, add the real homepage, privacy-policy and terms URLs plus the C-Sweet logo. Add the
relevant top private domain under **Authorized domains**. Verify domain ownership in
[Google Search Console](https://search.google.com/search-console/) using an account associated with
the Cloud project. Publish these pages before requesting production review; repository development
notices are not a substitute for our operating policies.
[Google branding and domain requirements](https://developers.google.com/identity/protocols/oauth2/production-readiness/brand-verification).

The privacy policy must describe any LLM/subprocessor use honestly. C-Sweet's product policy should
prohibit using customer provider data for general model training. Arrange appropriate processor terms
and deletion controls rather than assuming an API key authorizes every downstream use.

## 5 Add the permissions we actually use

Open **Google Auth platform → Data Access → Add or remove scopes**.

For the initial read-only connection, add:

| Permission | Scope |
| --- | --- |
| Verify the account identity | `openid` |
| Display its profile name | `profile` |
| Read YouTube channels/content | `https://www.googleapis.com/auth/youtube.readonly` |
| Read non-revenue analytics | `https://www.googleapis.com/auth/yt-analytics.readonly` |

Do not add `email` unless a released feature needs it. Only add these optional scopes when their
features are implemented and ready for review:

- Publishing: `https://www.googleapis.com/auth/youtube.upload`.
- Supported content/comment/live management: `https://www.googleapis.com/auth/youtube.force-ssl`.
- Eligible membership reads: `https://www.googleapis.com/auth/youtube.channel-memberships.creator`.

Keep `youtubepartner` out of the normal launch request. Content ID and partner features require their
own implementation, eligibility and provider review. Declaring a scope in the console is not consent;
the plugin requests only the subset needed for the feature the user chooses.
[YouTube scopes](https://developers.google.com/youtube/data/guides/auth/server-side-web-apps),
[Google Data Access setup](https://developers.google.com/workspace/guides/configure-oauth-consent).

Check the console's current sensitive/restricted classifications rather than assuming every
YouTube scope has the same review requirements.

## 6 Create the application credentials

1. Open **Google Auth platform → Clients → Create client**.
2. Choose **Web application**. Google connects to our server-side connection service, even when the
   customer uses C-Sweet on their own machine.
3. Give it a clear environment-specific name, such as **C-Sweet Connections Development**.
4. Under **Authorized redirect URIs**, add the exact environment callback below.
5. Create the client and securely save its client ID and client secret.

Proposed development callback:

```text
https://connections-dev.YOUR-DOMAIN/oauth/callback/com.csweet.google.youtube
```

Proposed production callback, in the production project/client only:

```text
https://connections.YOUR-DOMAIN/oauth/callback/com.csweet.google.youtube
```

These match the foundation service's `/oauth/callback/{profileId}` route, but no deployed hostname
has been selected. Confirm the final hostname/profile ID before registration. Google requires an
exact redirect match, including scheme/path. Use the local callback above for a local-backend test.
Do not register every customer's instance as a Google callback: our service receives Google's result
and returns a separate safe handoff to the customer's C-Sweet instance. The server-side flow does
not need an arbitrary JavaScript-origin allowlist.
[Google web-client registration](https://developers.google.com/identity/protocols/oauth2/web-server).

## 7 Put the secret in protected service configuration

Store the client secret in the managed service's secret manager, not Git, a plugin manifest, browser
code, customer installers, logs or chat. Give only the service workload identity access to it.
Register the client ID, exact endpoints, callback and vault reference in the service's approved
provider profile `com.csweet.google.youtube`. That profile name is a C-Sweet identifier, not something
Google creates or understands.

Google endpoints for that reviewed profile are:

```text
Authorization: https://accounts.google.com/o/oauth2/v2/auth
Token:         https://oauth2.googleapis.com/token
Revocation:    https://oauth2.googleapis.com/revoke
```

The implemented OAuth flows supply PKCE, state and offline authorization. You do not manually create
customer refresh tokens or API keys. Plan key rotation, access auditing and recovery.
[Google credential storage guidance](https://developers.google.com/identity/protocols/oauth2/resources/best-practices).

## 8 Test with your own account

After the service and host integration are deployed, use a test Google account with a channel:

1. Install the YouTube plugin and click Connect account.
2. Confirm Google's page identifies C-Sweet and shows only base access.
3. Accept access, return to the original plugin and check the imported channel's identity.
4. Confirm no business/agent has access until explicitly assigned.
5. Test cancellation, denial, reconnect and enabling an optional permission.
6. Disconnect, verify provider revocation and track deletion of the recorded copies.

External apps in Testing generally receive seven-day refresh tokens when requesting these YouTube
scopes. This is development behavior, not proof that reconnect logic is broken.
[Google refresh-token limitations](https://developers.google.com/identity/protocols/oauth2#expiration).

An account with multiple Brand Accounts may require additional authorizations. Never treat an
account label as proof that all its channels were imported. Workspace administrators may also
restrict third-party authorization.

## 9 Prepare the production project and review

Repeat the setup in the production project with real public policies and production-only callbacks.
Complete branding verification and publish the accepted branding. In **Verification Center**, submit
the required Data Access review with a reason for each sensitive scope and a demonstration of the
real English-language consent flow and each released feature using that access. Show the application
name and client identity clearly. Provide reviewer access/instructions and monitor follow-up emails.
[Google verification submission](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification).

If Google classifies requested scopes as restricted, determine the required assessment and renewal
obligations before release; do not assume scope consent alone completes that process.
[Restricted-scope requirements](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification).

Publishing an app and completing all required verification are separate tasks. Do not launch to
customers by instructing them to bypass unverified-app warnings. Review changes when adding scopes,
features, domains or subprocessors.

## 10 Confirm YouTube capacity and policy readiness

Check the production project's actual quota allocations in Cloud Console. Plan per-customer budgets
and service-wide limits. Request more capacity through YouTube's **Audit and Quota Extension Form**;
this is separate from OAuth verification. Do not create extra projects to circumvent limits.
[YouTube quota and compliance process](https://developers.google.com/youtube/data/guides/quota_and_compliance_audits).

Google's current upload reference says unverified projects are not restricted to private viewing,
while its historical 2020 revision entry describes a private-upload restriction. Do not turn that
historical rule into a new hard-coded product gate or assume public uploading is verified. Confirm
the production project's behavior with an explicitly approved test and ask YouTube to resolve any
remaining applicability question during review. Always set the intended privacy explicitly.
[Current upload reference](https://developers.google.com/youtube/data/reference/rest/v3/videos/insert),
[historical revision record](https://developers.google.com/youtube/data/revision_history#july-28-2020).

Keep an accessible disconnect/delete route, YouTube Terms and Google privacy links, and disclose
data storage/sharing. Implement the agreed deletion deadlines across the service and customer copies.
Sell the C-Sweet application features, not unrestricted access to Google's APIs.
[YouTube policies](https://developers.google.com/youtube/terms/developer-policies).

## Optional customer-owned setup

An independent self-hosted administrator repeats the registration using their own project and
configures the local vault profile in Advanced connection settings. For the current local flow,
register their deployment's exact HTTPS `/api/plugin-connections/oauth/callback` address instead of
the managed-service callback. Ordinary users on that deployment still just click Connect account.

Do not copy C-Sweet's managed secret into that installation. Customer-owned mode and managed mode
use separate authorizations; switching between them requires fresh connection review.

## What you need to provide first

- The domain/subdomain we will use for the development and production connection services.
- The support/developer contact emails and company-controlled Google project ownership.
- Public homepage/privacy/terms URLs and the approved logo.
- The client IDs, project IDs/numbers and callback addresses as deployment metadata.
- Client secrets delivered directly into the chosen secret manager, never this chat.

You can register and test the local development flow now. Testing the customer managed-service
experience and recording its production review demonstration require host integration and deployment.
