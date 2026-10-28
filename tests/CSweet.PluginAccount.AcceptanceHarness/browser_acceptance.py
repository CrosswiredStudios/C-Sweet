"""Offline native-page acceptance. Run the localhost harness first; never contacts Google.

The package build/install is a stub; OAuth, import and grants use real account services
with synthetic identity-provider and resource responses. No real credentials are used.
"""
import argparse
from pathlib import Path
from urllib.parse import parse_qs, quote, urlparse
from playwright.sync_api import sync_playwright, expect

parser = argparse.ArgumentParser()
parser.add_argument("--base", default="http://127.0.0.1:5279")
parser.add_argument("--output", required=True)
args = parser.parse_args()
out = Path(args.output)
out.mkdir(parents=True, exist_ok=True)

with sync_playwright() as p:
    browser = p.chromium.launch(headless=True)
    page = browser.new_page(viewport={"width": 1280, "height": 1000})
    errors = []
    page.on("pageerror", lambda e: errors.append(str(e)))

    def provider(route):
        params = parse_qs(urlparse(route.request.url).query)
        assert params["code_challenge_method"] == ["S256"]
        assert "client_secret" not in params
        target = args.base + "/test/provider-return?state=" + quote(params["state"][0])
        route.fulfill(content_type="text/html", body=f'''<!doctype html><html><body>
            <h1>Example provider — synthetic consent only</h1>
            <p>Test account. Requested scopes: {params['scope'][0]}</p>
            <a href="{target}">Allow test connection</a></body></html>''')

    page.route("https://identity.example.com/authorize?**", provider)
    assert page.request.post(args.base + "/test/reset").status == 204
    page.goto(args.base + "/settings/plugins")
    page.get_by_test_id("interactive-ready").wait_for(state="attached")
    page.get_by_role("tab", name="Install", exact=True).click()
    expect(page.get_by_role("tab", name="Install", exact=True)).to_have_attribute("aria-selected", "true")
    page.screenshot(path=str(out / "01-install-source.png"), full_page=True)
    page.get_by_role("textbox", name="Plugin repository URL").fill("https://github.com/example/example-connection")
    page.get_by_role("button", name="Preview plugin", exact=True).click()
    expect(page.get_by_text("Installing makes these abilities available", exact=False)).to_be_visible()
    expect(page.get_by_text("Install for", exact=True)).to_have_count(0)
    page.screenshot(path=str(out / "02-review-grants.png"), full_page=True)
    page.get_by_role("checkbox", name="I approve the displayed", exact=False).check()
    page.get_by_role("button", name="Install plugin", exact=True).click()
    expect(page).to_have_url(__import__('re').compile(r"/settings/plugins/[a-f0-9-]{36}$"))
    expect(page.get_by_text("Connect your first account", exact=True)).to_be_visible()
    expect(page.get_by_text("Some deployment cleanup services are not configured.",exact=False)).not_to_be_visible()
    expect(page.get_by_role("button",name="Connect account",exact=True)).to_have_count(1)
    expect(page.get_by_role("button",name="Connect account",exact=True).first).to_be_enabled()
    page.screenshot(path=str(out / "03-installed-empty.png"), full_page=True)
    page.get_by_text("Plugin settings",exact=True).click()
    expect(page.get_by_text("Some deployment cleanup services are not configured.",exact=False)).to_be_visible()
    page.get_by_text("Verification diagnostics and updates",exact=True).click()
    page.get_by_role("button",name="View verification diagnostics",exact=True).click()
    expect(page.get_by_text("Synthetic package build diagnostics.",exact=False)).to_be_visible()
    page.get_by_role("button",name="Check for update",exact=True).click()
    expect(page.get_by_text("This repository currently resolves to the installed version.",exact=True)).to_be_visible()
    page.get_by_text("Verification diagnostics and updates",exact=True).click()
    page.get_by_text("Plugin settings",exact=True).click()
    expect(page.get_by_role("region", name="Account connection access and policies")).to_be_visible()
    expect(page.get_by_role("link", name="Privacy policy", exact=True)).to_be_visible()
    page.get_by_role("button", name="Connect account", exact=True).first.click()
    expect(page.get_by_role("heading",name="Example provider",exact=False)).to_be_visible()
    expect(page.get_by_text("Requested scopes: identity read", exact=False)).to_be_visible()
    expect(page.get_by_text("optional", exact=False)).to_have_count(0)
    page.screenshot(path=str(out / "04-synthetic-provider-consent.png"), full_page=True)
    page.get_by_role("link",name="Allow test connection").click()
    page.get_by_test_id("interactive-ready").wait_for(state="attached")
    expect(page.get_by_text("Workspace A",exact=True)).to_be_visible()
    expect(page.get_by_text("Workspace B",exact=True)).to_be_visible()
    expect(page.get_by_text("Account connected and accessible resources imported.",exact=False)).to_be_visible()
    page.screenshot(path=str(out / "05-account-imported.png"), full_page=True)
    page.get_by_text("Plugin settings",exact=True).click()
    page.get_by_text("Connection operators",exact=True).click()
    page.get_by_role("combobox",name="Operator user",exact=True).click()
    page.get_by_role("option",name="manager",exact=True).click()
    page.get_by_role("combobox",name="Accounts this operator may manage",exact=True).click()
    page.get_by_role("option",name="Example account",exact=True).click()
    page.get_by_role("button",name="Assign operator",exact=True).click()
    expect(page.get_by_role("button",name="Revoke operator",exact=True)).to_be_visible()
    page.screenshot(path=str(out / "06a-account-operator.png"),full_page=True)
    page.get_by_role("button",name="Revoke operator",exact=True).click()
    page.get_by_role("button",name="Confirm revoke operator",exact=True).click()
    expect(page.get_by_role("button",name="Revoke operator",exact=True)).to_have_count(0)
    page.get_by_text("Connection operators",exact=True).click()
    page.get_by_role("button", name="Assign business access").click()
    page.get_by_role("combobox",name="Business",exact=True).click()
    page.get_by_role("option",name="Business A",exact=True).click()
    page.get_by_role("checkbox",name="Workspace A",exact=True).check()
    page.get_by_role("button",name="Save access",exact=True).click()
    expect(page.get_by_text("Business access saved.",exact=False)).to_be_visible()
    page.screenshot(path=str(out / "06-selected-business-access.png"),full_page=True)
    page.get_by_role("button",name="Pause plugin",exact=True).click()
    page.get_by_role("button",name="Confirm pause",exact=True).click()
    expect(page.get_by_role("button",name="Connect another account",exact=True)).to_have_count(0)
    page.get_by_role("button",name="Resume plugin",exact=True).click()
    page.get_by_role("button",name="Confirm resume",exact=True).click()
    expect(page.get_by_role("button",name="Connect another account",exact=True)).to_be_enabled()
    page.get_by_role("button",name="Reconnect / change permissions").click()
    page.get_by_role("checkbox",name="Optional",exact=True).check()
    page.get_by_role("checkbox",name="I have reviewed the access",exact=False).check()
    page.get_by_role("button",name="Continue to secure sign-in").click()
    page.get_by_role("link",name="Allow test connection").click()
    page.get_by_test_id("interactive-ready").wait_for(state="attached")
    expect(page.get_by_text("Read, Optional",exact=False)).to_be_visible()
    page.get_by_role("button",name="Assign business access").click()
    page.get_by_role("combobox",name="Business",exact=True).click()
    page.get_by_role("option",name="Business A",exact=True).click()
    page.get_by_role("radio",name="Entire account",exact=True).check()
    expect(page.get_by_role("button",name="Save access",exact=True)).to_be_disabled()
    page.get_by_role("checkbox",name="I understand this business",exact=False).check()
    page.get_by_role("button",name="Save access",exact=True).click()
    expect(page.get_by_text("Entire account, including future imports",exact=False)).to_be_visible()
    page.get_by_role("button",name="Remove access",exact=True).click()
    expect(page.get_by_text("This business and its agents will immediately lose access",exact=False)).to_be_visible()
    page.get_by_role("button",name="Confirm remove access",exact=True).click()
    expect(page.get_by_text("Business access removed.",exact=False)).to_be_visible()
    page.set_viewport_size({"width":390,"height":844})
    page.screenshot(path=str(out / "07-mobile-account.png"),full_page=True)
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Page overflows narrow viewport"
    assert page.request.post(args.base+"/test/seed-synthetic-memory").status==204
    page.get_by_role("button",name="Disconnect account",exact=True).click()
    # Revoked access does not erase retained-data obligations for a business.
    expect(page.get_by_text("Affected businesses: Business A",exact=True)).to_be_visible()
    page.screenshot(path=str(out / "08-disconnect-impact.png"),full_page=True)
    page.get_by_role("button",name="Confirm disconnect",exact=True).click()
    expect(page.get_by_text("Deletion is not yet confirmed.",exact=False)).to_be_visible()
    assert page.request.post(args.base+"/test/run-cleanup").status==204
    assert page.request.get(args.base+"/test/memory-confirmation-count").json()==0
    # Provider consent navigates away; returning starts with administrator details collapsed.
    if page.locator("#plugin-settings").get_attribute("open") is None:
        page.get_by_text("Plugin settings",exact=True).click()
    page.get_by_role("button",name="Refresh status",exact=True).click()
    expect(page.get_by_text("Tracked primary database copies removed.",exact=False)).to_be_visible()
    expect(page.get_by_text("Disconnected resource",exact=True)).to_have_count(0)
    page.get_by_role("button",name="Remove plugin",exact=True).click()
    page.get_by_role("button",name="Confirm remove",exact=True).click()
    expect(page.get_by_text("Removal is in progress.",exact=False)).to_be_visible()
    page.screenshot(path=str(out/"09-cleanup-confirmations-pending.png"),full_page=True)
    assert page.request.post(args.base+"/test/confirm-synthetic-cleanup").status==204
    assert page.request.get(args.base+"/test/cleanup-receipt-count").json()==1
    assert page.request.get(args.base+"/test/memory-confirmation-count").json()==1
    page.get_by_role("button",name="Refresh status",exact=True).click()
    expect(page.get_by_text("Plugin removed.",exact=False)).to_be_visible()
    expect(page.get_by_text("All recorded cleanup obligations confirmed.",exact=True)).to_be_visible()
    page.screenshot(path=str(out/"10-removed-synthetic-receipts.png"),full_page=True)
    # Exercise the actual browser return module, without contacting the managed service or Google.
    result = page.evaluate("""async () => {
        const module = await import('/_content/CSweet.UI/js/managed-plugin-return.js');
        const attempt = '4c643638-094c-4971-ae60-bef6071ad55c';
        const handoff = 'h'.repeat(43);
        history.replaceState(null, '', '/settings/plugins/managed-return#handoff=' + handoff + '&attempt=' + attempt);
        const value = module.consumeReturn();
        if (value.handoff !== handoff || value.attemptId !== attempt || location.hash || location.search) throw Error('Return was not consumed safely');
        for (const fragment of [
            '#handoff=' + handoff + '&attempt=' + attempt + '&attempt=' + attempt,
            '#handoff=bad&attempt=' + attempt,
            '#handoff=' + handoff + '&attempt=' + attempt + '&redirect=https://attacker.example',
        ]) {
            history.replaceState(null, '', '/settings/plugins/managed-return' + fragment);
            let rejected = false;
            try { module.consumeReturn(); } catch { rejected = true; }
            if (!rejected || location.hash) throw Error('Unsafe return accepted or retained');
        }
        return true;
    }""")
    assert result
    assert not errors, errors
    print("PASS: non-gated install/connect with missing cleanup services, consent, import, scoped operators, selected/whole business grants, pause/resume, progressive consent, mobile layout, disconnect, pending cleanup, removal, signed backup receipt and native memory confirmation. Provider/package install/erasure receipt data and external confirmations are synthetic.")
    browser.close()
