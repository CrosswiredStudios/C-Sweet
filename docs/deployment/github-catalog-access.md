# GitHub catalog access

Agent hiring reads repository metadata, resolves the selected reference, and downloads
plugin resources through the GitHub REST API before a build or clone starts.
Without authentication, those requests share the server's public-IP allowance of
60 requests per hour. Connecting a business GitHub App does not authenticate this
separate catalog client.

Configure `GitHubAgentRepository:AccessToken` in trusted host secret configuration,
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

See [GitHub REST API rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).
