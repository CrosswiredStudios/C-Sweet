# GitHub catalog access

Agent hiring reads repository metadata, resolves the selected reference, and downloads
plugin resources through the GitHub REST API before a build or clone starts.
Without authentication, those requests share the server's public-IP allowance of
60 requests per hour. Connecting a business GitHub App does not authenticate this
separate catalog client.

Configure `GitHubAgentRepository:AccessToken` in the API host's secret configuration,
or supply the environment variable `GitHubAgentRepository__AccessToken` to the API
process. Use a dedicated GitHub token with only the public repository read access
needed by the catalog. Do not put the token in checked-in settings or browser
configuration. Restart the API after changing its environment configuration.

An authenticated personal access token normally provides 5,000 REST requests per
hour, shared with other uses of that account. GitHub secondary limits still apply.
An expired or revoked token must be replaced; the client does not silently fall
back to anonymous access.

If GitHub throttles a request, the catalog displays the `Retry-After` delay or
`X-RateLimit-Reset` time when supplied. Wait until that time before retrying the
hire. Repeated immediate retries do not restore the allowance.

See [GitHub REST API rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api).
