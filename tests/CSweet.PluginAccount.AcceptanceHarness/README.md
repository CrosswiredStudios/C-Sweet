# Native plugin account acceptance harness

Development/test only. Bind to loopback; never deploy this project. It exposes reset and synthetic
provider-return endpoints without product authentication. It is not part of the production app.

The harness hosts the actual `Plugins` and `PluginManage` Razor components with real
`PluginAccountService` OAuth-attempt/import/business-grant/disconnect logic and in-memory synthetic
identity/resource responses. Package preview/build/install is simulated; production endpoints,
authentication, PostgreSQL concurrency, Google and the complete agent approval UI are **not** exercised.
Service tests separately cover the actual enterprise installer and business/resource/agent binding.
The reset fixture explicitly uses a Production account-service environment, with the legacy preview
flag disabled and cleanup adapters initially missing. Missing services must not gate account consent.

Run `dotnet run --project tests/CSweet.PluginAccount.AcceptanceHarness --urls http://127.0.0.1:5279`
with the same package-only SDK/WorkManagement flags and local verified package feed used for host
release tests. Use an isolated `--artifacts-path` if other developers are building sibling sources.

Then run `python tests/CSweet.PluginAccount.AcceptanceHarness/browser_acceptance.py --output <test-output>`
with Playwright/Chromium available. The script intercepts only `https://identity.example.com/authorize`
and renders an explicit synthetic consent page. It never contacts Google or uses real credentials.
It resets only its own in-memory fixtures. Screenshots and assertions cover install review, enterprise
install, consent, import, selected and whole-account grants, additional permission consent, local
revocation, narrow-screen layout and disconnect impact/pending deletion. It checks for browser errors.
It also exercises scoped operator assignment/revocation, collapsed administrative controls, synthetic
build diagnostics/no-update feedback, pause/resume, removal blocked on cleanup, and eventual retired
state after explicitly synthetic confirmations. `/test/run-cleanup` runs actual durable processing;
`/test/confirm-synthetic-cleanup` uses the production backup signed-receipt verifier with an ephemeral
synthetic RSA key/gateway; the script asserts that its immutable receipt is persisted. It also seeds
content-free synthetic memory lineage, verifies pending cleanup, then supplies a synthetic native
erasure receipt to the real memory-confirmation participant. This does not execute real PostgreSQL
memory erasure. Other stores, if present, still use test-only acknowledgements.
`/test/cleanup-receipt-count` and `/test/memory-confirmation-count` expose only synthetic counts.
None of these endpoints belongs to
the product. Staged update approval has service-level tests, not full browser/build coverage here.

Production acceptance still requires authorized test accounts, the real deployed host, approved
package installation, registered/verified OAuth application, restrictive account grants, native agent
binding/action approvals and independently verified data deletion. Passing this harness is not that evidence.
