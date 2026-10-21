const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { resolveRequest, serverFor } = require('../build/compute-linux/browser/runner.cjs');

test('static server serves assets with correct types and rejects invalid paths', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'csweet-browser-server-'));
  const server = serverFor(root);
  try {
    await fs.writeFile(path.join(root, 'index.html'), '<canvas></canvas>');
    await fs.writeFile(path.join(root, 'game.js'), 'window.loaded = true;');
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    assert.equal(await (await fetch(origin)).text(), '<canvas></canvas>');
    const asset = await fetch(origin + '/game.js');
    assert.equal(asset.status, 200);
    assert.equal(asset.headers.get('content-type'), 'text/javascript');
    assert.equal((await fetch(origin + '/missing.js')).status, 404);
    assert.equal((await fetch(origin + '/%ZZ')).status, 400);
    assert.equal((await fetch(origin + '/game.js', { method: 'POST' })).status, 405);
    assert.throws(() => resolveRequest(root, '/%2e%2e/outside'));
    assert.throws(() => resolveRequest(root, '/bad%00name'));
    assert.throws(() => resolveRequest(root, '/bad%5cname'));
  } finally {
    await new Promise(resolve => server.close(resolve));
    await fs.rm(root, { recursive: true, force: true });
  }
});
