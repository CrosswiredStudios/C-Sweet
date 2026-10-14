import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/CSweet.UI/wwwroot/js/auth-security.js', import.meta.url), 'utf8');
const { printText } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('blocked printing returns false so the registration form can offer alternatives', () => {
    globalThis.window = { open: () => null };
    assert.equal(printText('Recovery codes', 'DEMO'), false);
    delete globalThis.window;
});

test('printing detaches the opener and inserts recovery codes as text', () => {
    const elements = [];
    let printed = false;
    const popup = {
        opener: {},
        document: { createElement: tag => ({ tag }), body: { append: (...nodes) => elements.push(...nodes) } },
        print: () => { printed = true; }
    };
    globalThis.window = { open: (url, target) => { assert.equal(url, ''); assert.equal(target, '_blank'); return popup; } };
    assert.equal(printText('Recovery codes', '<test-code>'), true);
    assert.equal(popup.opener, null);
    assert.equal(popup.document.title, 'Recovery codes');
    assert.deepEqual(elements, [{ tag: 'h1', textContent: 'Recovery codes' }, { tag: 'pre', textContent: '<test-code>' }]);
    assert.equal(printed, true);
    delete globalThis.window;
});
