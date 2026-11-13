# GitHub catalog access

Agent hiring reads repository metadata, resolves the selected reference, and downloads
plugin resources through the GitHub REST API before a build or clone starts.
Without authentication, those requests share the server's public-IP allowance of
60 requests per hour. Connecting a business GitHub App does not authenticate this
separate catalog client.

An administrator can sign in from Settings, Source control, under "GitHub sign-in for downloads": paste a GitHub
access token from a personal or an organization account. C-Sweet asks GitHub which account the token belongs to
before saving it, stores it encrypted with the shared data-protection key ring in the `GitHubCatalogCredentials`
table, and shows the account, the remaining hourly allowance and the token's expiry. The API never returns the
token. API and AgentHost both read it, within a minute of a change and without a restart, and it takes precedence
over host configuration. Removing it returns to host configuration or anonymous access. Read-only access to public
repositories is enough for the public catalog (2026-11-13).

When resource setup is rate-limited and no credential is configured, each active person in the business receives
one "Sign in to GitHub to keep setup moving" notification a day that opens that section. No notification is sent
when a credential exists, because the limit is then the account's own.

Host configuration remains available for unattended installations. Configure `GitHubAgentRepository:AccessToken` in trusted host secret configuration,
or supply `GitHubAgentRepository__AccessToken` to both API and AgentHost. With Aspire,
configure the setting in `src/CSweet.AppHost` user secrets; AppHost forwards it as a
secret parameter to both processes. Restart AppHost after changing the setting.
Use a dedicated token restricted to the required repositories with metadata and
contents read access. Private provider repositories require explicit repository
access. Do not put credentials in checked-in settings, browser configuration or agent
manifests. An agent never receives the catalog credential.

`GitHubReleaseAssetAuthenticationHandler` authenticates downloads only through the
HTTPS `api.github.com/repos/.../releases/assets/...` endpoint and requests binary
content. Published metadata retains that route for both bundles and checksum sidecars;
credentials are not sent to arbitrary download hosts. Checksums are resolved from the
reviewed release tag, not from whichever version happens to be latest.

An inaccessible private repository can return 404, so that response does not prove
that a release is absent. Resource preparation retains a visible dependency condition
and retries after access is corrected, honoring provider retry timing. Invalid bundles
remain failed verification and cannot enter approval or installation.

An authenticated personal access token normally provides 5,000 REST requests per
hour, shared with other uses of that account. GitHub secondary limits still apply.
An expired or revoked token must be replaced; the client does not silently fall
back to anonymous access.

If GitHub throttles a request, the catalog displays the `Retry-After` delay or
`X-RateLimit-Reset` time when supplied. Wait until that time before retrying the
hire. Repeated immediate retries do not restore the allowance.

Release bundles and their checksum files are requested through the API asset route first. If GitHub refuses
that request with 403 or 429, the same asset is requested from the public release route on `github.com`
(`/releases/download/{tag}/{asset}`), which uses no REST API allowance. The catalog credential is never sent to
that route, and a private repository's assets are not available there. A refused download now reports the
`X-RateLimit-Reset` time as well as `Retry-After`, so resource setup waits for the reset instead of retrying
every minute (2026-11-13).

One agent import reads the default branch, the commit, the manifest, up to three release lookups and one file per
declared definition, then the release, the bundle and its checksum: roughly eight to eleven requests. Hiring a
team of eight on a fresh installation therefore exceeds the anonymous allowance of 60 requests per hour by itself.

See [GitHub REST API rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).
