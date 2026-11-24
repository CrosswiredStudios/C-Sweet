from pathlib import Path
import os
import re
from playwright.sync_api import sync_playwright, expect

out = Path("artifacts/selection-browser")
out.mkdir(parents=True, exist_ok=True)
base = os.environ.get("CSWEET_SELECTION_TEST_URL", "http://127.0.0.1:5391")

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True, channel=os.environ.get("CSWEET_BROWSER_CHANNEL"))
    page = browser.new_page()

    def visit(path=""):
        page.goto(base + path)
        page.get_by_test_id("interactive-ready").wait_for(state="attached")

    def click(name):
        page.get_by_role("button", name=name).click()

    visit()
    hosted = page.get_by_role("button", name="Use C-Sweet hosting")
    expect(hosted.get_by_text("Free", exact=True)).to_be_visible()
    click("Decide later")
    expect(page.get_by_test_id("continued")).to_have_text("1")
    click("Back")
    click("Return to connections")
    click("Use C-Sweet hosting")
    click("Save and check availability")
    expect(page.get_by_text("Couldn’t verify this service", exact=False)).to_be_visible()
    click("Back")
    click("Host my own")
    click("On another server")
    page.get_by_label("Service address").fill("https://connections.example.test")
    with page.expect_download() as downloading:
        page.get_by_role("link", name="Download compose.yaml").click()
    download = downloading.value
    assert download.suggested_filename == "compose.yaml"
    assert "https://connections.example.test" in Path(download.path()).read_text(encoding="utf-8")
    click("Back")
    click("Connect a running service")
    click("Save and check availability")
    expect(page.get_by_text("Couldn’t verify this service", exact=False)).to_be_visible()
    page.locator("summary", has_text="Connecting your phone?").click()
    click("Check phone setup")
    expect(page.get_by_text("Link this Headquarters", exact=False)).to_be_visible()
    click("Finish later in Settings")
    expect(page.get_by_test_id("continued")).to_have_text("2")
    visit("/settings")
    expect(page.locator("strong", has_text="https://connections.example.test")).to_be_visible()
    click("Turn off app connections")
    visit("/settings")
    expect(page.get_by_text("Current choice", exact=False)).to_be_visible()
    expect(page.get_by_role("button", name="Prepare pairing")).to_have_count(0)
    visit()
    click("Host my own")
    click("Connect a running service")
    page.get_by_label("Service address").fill("https://ready.example.test")
    click("Save and check availability")
    click("Prepare pairing")
    expect(page.get_by_label("Public pairing details")).to_have_value(re.compile("synthetic-public-key"))
    page.get_by_label("Deployment ID returned by service").fill("22222222-2222-2222-2222-222222222222")
    page.get_by_label("Tenant ID returned by service").fill("fixture-tenant")
    click("Verify and finish pairing")
    expect(page.get_by_text("This installation is paired.", exact=False)).to_be_visible()
    browser.close()
print("Passed hosted, deferred, remote download, existing service, phone guidance and saved Settings flows.")
