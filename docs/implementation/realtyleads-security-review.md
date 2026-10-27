# RealtyLeadsAI final integration review

Reviewed the SDK declaration/validator, native entry API and UI, encrypted repository,
current-owner authorization, consumer binding resolver, catalog and gateway dispatch,
import/build/install paths, transport and result projection. This is a source and
fixture review, not a production penetration test or live provider identity proof.

## Findings addressed

- Broker result fields previously accepted any string/number/null before downstream
  schema validation. Enforce text fields versus finite numeric score inside the broker
  itself. Three synthetic malformed-response cases now prove rejection there.
- The generic source-archive importer classified an unrecognized provider kind as a
  service. Reject reviewed remote-MCP archives before persistence so they cannot
  enter the executable service build path. Initial support requires immutable
  repository import. Executable bundle imports already reject this mode.
- Add an isolated SQLite relational test of a stale tracked account writer racing a
  revocation. The database revision concurrency token must reject the old update,
  and the revoked generation must remain authoritative.

## Boundaries checked

- Native authentication identifies the owner; active human Manager membership is
  checked at administration and runtime boundaries. Another authorized manager does
  not inherit access to a connection. Business and installation are checked in the
  repository query as well as the Data Protection purpose.
- Connection creation grants no consumer. Explicit native consent binds the reviewed
  provider digest and consumer grants; model arguments never resolve account selection.
  Duplicate concurrent bindings fail closed. Rotation/revocation invalidates generations,
  bindings, sessions and queued work; in-flight results recheck current authority.
- Key entry uses a bounded request, write-only password field and no key-returning API.
  Production API also applies its existing antiforgery middleware. The additional
  native header is defense in depth, not a replacement for authentication/antiforgery.
  The application registers the existing cookie/antiforgery HTTP handler.
- Broker-only injection targets exactly https://realtyleads.ai/mcp, disables redirects,
  cookies/proxies and rejects nonpublic or unsupported DNS destinations. JSON/SSE,
  timeout, byte, depth, tool, input and output limits fail closed. Raw remote errors,
  schema descriptions, headers and session identifiers are never workload outputs.
- Known literal/decoded/URI/Base64 key echoes are withheld. This is not a claim to
  detect every possible arbitrary covert encoding by a malicious provider. The
  provider is trusted with the credential by necessity; only reviewed lead fields
  are released to the agent.
- OAuth connector validation is preserved; the distinct remote mode has a closed
  versioned validator, no runtime code or general network/configuration credentials.
  Repository verification and host-only installation create no provider workload.
- Data Protection uses the existing host key-ring configuration. Protect the database,
  host and backups; encryption here is not a separate hardware key vault and does not
  defend against a compromised trusted host/database-plus-key-ring administrator.

## Verification limits

See `realtyleads-remote-mcp.md` and the regression report for exact test results.
The browser harness uses synthetic authentication, an SDK test runtime and fake MCP;
production cookie/antiforgery, deployed workload identity and live authenticated
provider behavior remain development-rollout checks. Provider company identity is
not discoverable from this contract and is enforced by RealtyLeadsAI. No real key,
new grant, production service or running container was used during this review.
