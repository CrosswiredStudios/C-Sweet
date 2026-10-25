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
    page.screenshot(path=str(out / "03-installed-empty.png"), full_page=True)
    page.get_by_role("button", name="Connect account", exact=True).first.click()
    expect(page.get_by_role("button", name="Continue to secure sign-in")).to_be_disabled()
    page.get_by_role("checkbox", name="I have reviewed the access", exact=False).check()
    page.get_by_role("button", name="Continue to secure sign-in").click()
    expect(page.get_by_role("heading",name="Example provider",exact=False)).to_be_visible()
    page.screenshot(path=str(out / "04-synthetic-provider-consent.png"), full_page=True)
    page.get_by_role("link",name="Allow test connection").click()
    page.get_by_test_id("interactive-ready").wait_for(state="attached")
    expect(page.get_by_text("Workspace A",exact=True)).to_be_visible()
    expect(page.get_by_text("Workspace B",exact=True)).to_be_visible()
    expect(page.get_by_text("Account connected and accessible resources imported.",exact=False)).to_be_visible()
    page.screenshot(path=str(out / "05-account-imported.png"), full_page=True)
    page.get_by_role("button", name="Assign business access").click()
    page.get_by_role("combobox",name="Business",exact=True).click()
    page.get_by_role("option",name="Business A",exact=True).click()
    page.get_by_role("checkbox",name="Workspace A",exact=True).check()
    page.get_by_role("button",name="Save access",exact=True).click()
    expect(page.get_by_text("Business access saved.",exact=False)).to_be_visible()
    page.screenshot(path=str(out / "06-selected-business-access.png"),full_page=True)
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
    page.get_by_role("button",name="Disconnect account",exact=True).click()
    expect(page.get_by_text("Affected businesses: None",exact=True)).to_be_visible()
    page.screenshot(path=str(out / "08-disconnect-impact.png"),full_page=True)
    page.get_by_role("button",name="Confirm disconnect",exact=True).click()
    expect(page.get_by_text("Deletion is not yet confirmed.",exact=False)).to_be_visible()
    assert not errors, errors
    print("PASS: install review, enterprise install, consent, paginated import, selected business access, progressive reconnect, local revoke, mobile layout, disconnect impact and honest pending deletion. Fake provider and simulated package install only.")
    browser.close()
