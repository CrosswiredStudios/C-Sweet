# Native synthetic acceptance

Test-only web host using the production native account page and API endpoints,
in-memory database and ephemeral vault protection, fake authenticated owner, actual
RealtyLeadsAgent callbacks via AgentTestRuntime, and fake MCP transport. Never deploy
this harness or enter real credentials. It is not a live workload-VM/provider test.
The agent source link requires the sibling `CSweet.Agent.RealtyLeadsAI` checkout.

From the four-repository workspace, with SDK 5.6.0 restored:

```sh
dotnet run --project C-Sweet/tests/CSweet.RemoteMcp.AcceptanceHarness --urls http://127.0.0.1:5287
```

In another terminal, with Python Playwright installed and Chrome available:

```sh
python C-Sweet/tests/CSweet.RemoteMcp.AcceptanceHarness/browser_acceptance.py --output acceptance-evidence
```

Use `--chrome` for a different Chrome executable path. The default is macOS Chrome.
The script submits a fixed synthetic key, checks owner/header/body bounds and no
readback, then proves connect → explicit consent/grant → agent read → revoke →
denied. It captures three screenshots and fails on browser JavaScript errors.
Stop the harness after testing; no external MCP call or persistent real grant occurs.
