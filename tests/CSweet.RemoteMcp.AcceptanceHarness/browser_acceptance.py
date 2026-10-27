"""Run against the isolated localhost harness only. Requires Playwright and Chrome.
Uses the production native page/endpoints and actual agent with fake MCP responses.
"""
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

parser = argparse.ArgumentParser()
parser.add_argument('--base', default='http://127.0.0.1:5287')
parser.add_argument('--chrome', default='/Applications/Google Chrome.app/Contents/MacOS/Google Chrome')
parser.add_argument('--output', required=True)
args = parser.parse_args()
assert args.base.startswith('http://127.0.0.1:')
out = Path(args.output); out.mkdir(parents=True, exist_ok=True)
with sync_playwright() as p:
    browser = p.chromium.launch(headless=True, executable_path=args.chrome)
    page = browser.new_page(viewport={'width': 1200, 'height': 1000})
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    identity = page.request.get(args.base + '/test/identity').json()
    path = f"{args.base}/api/businesses/{identity['business']}/plugins/{identity['provider']}/api-key-accounts/"
    assert page.request.get(path, headers={'X-Fixture-Other-User':'1'}).status == 403
    assert page.request.post(path, data={'key':'synthetic-native-account-key-0001'}).status == 403
    assert page.request.post(path, data={'key':'x'*5000}, headers={'X-CSweet-Native-Account':'1'}).status == 413
    assert page.request.post(args.base + '/test/agent-read').status == 403
    page.goto(args.base)
    page.get_by_test_id('interactive-ready').wait_for(state='attached')
    key = page.get_by_label('API key', exact=True)
    expect(key).to_have_attribute('type','password')
    key.fill('synthetic-native-account-key-0001')
    page.get_by_role('button', name='Store a new connection', exact=True).click()
    expect(page.get_by_text('Stored · no agent access', exact=True)).to_be_visible()
    expect(key).to_have_value('')
    assert 'synthetic-native-account-key-0001' not in page.content()
    assert page.request.post(args.base + '/test/agent-read').status == 403
    assert 'synthetic-native-account-key-0001' not in page.request.get(path).text()
    page.screenshot(path=str(out/'01-connected-no-grant.png'), full_page=True)
    page.get_by_role('combobox', name='Agent to authorize').click()
    page.get_by_role('option', name='RealtyLeadsAI Researcher').click()
    grant = page.get_by_role('button', name='Grant read access', exact=True)
    expect(grant).to_be_disabled()
    page.get_by_role('checkbox', name='Allow this agent to search', exact=False).check()
    grant.click()
    expect(page.get_by_text('Access granted to the selected agent', exact=True)).to_be_visible()
    assert page.request.post(args.base + '/test/agent-read').status == 200
    page.screenshot(path=str(out/'02-granted-agent-read.png'), full_page=True)
    page.get_by_role('button', name='Revoke access and remove stored key').click()
    expect(page.get_by_text('Revoked', exact=True)).to_be_visible()
    assert page.request.post(args.base + '/test/agent-read').status == 403
    page.screenshot(path=str(out/'03-revoked-agent-denied.png'), full_page=True)
    assert not errors, errors
    browser.close()
print('PASS: native connect -> explicit grant -> actual agent read -> revoke -> denied; no key readback; owner/CSRF/size checks')
