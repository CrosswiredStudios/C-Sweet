# Native plugin account acceptance harness

Development/test only. Bind to loopback; never deploy this project. It exposes reset and synthetic
provider-return endpoints without product authentication. It is not part of the production app.

The harness hosts the actual `Plugins` and `PluginManage` Razor components with real
`PluginAccountService` OAuth-attempt/import/business-grant/disconnect logic and in-memory synthetic
identity/resource responses. Package preview/build/install is simulated; production endpoints,
authentication, PostgreSQL concurrency, Google and the complete agent approval UI are **not** exercised.
Service tests separately cover the actual enterprise installer and business/resource/agent binding.

Run `dotnet run --project tests/CSweet.PluginAccount.AcceptanceHarness --urls http://127.0.0.1:5279`
with the same package-only SDK/WorkManagement flags and local verified package feed used for host
release tests. Use an isolated `--artifacts-path` if other developers are building sibling sources.

Then run `python tests/CSweet.PluginAccount.AcceptanceHarness/browser_acceptance.py --output <test-output>`
with Playwright/Chromium available. The script intercepts only `https://identity.example.com/authorize`
and renders an explicit synthetic consent page. It never contacts Google or uses real credentials.
It resets only its own in-memory fixtures. Screenshots and assertions cover install review, enterprise
install, consent, import, selected and whole-account grants, additional permission consent, local
revocation, narrow-screen layout and disconnect impact/pending deletion. It checks for browser errors.

Production acceptance still requires authorized test accounts, the real deployed host, approved
package installation, registered/verified OAuth application, restrictive account grants, native agent
binding/action approvals and independently verified data deletion. Passing this harness is not that evidence.
