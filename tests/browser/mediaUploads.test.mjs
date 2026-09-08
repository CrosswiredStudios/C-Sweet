import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// Load the browser module without changing application/package module configuration.
const source = await readFile(new URL('../../src/CSweet.UI/wwwroot/js/mediaUploads.js', import.meta.url), 'utf8');
const uploads = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const values = new Map();
globalThis.sessionStorage = {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, value),
    removeItem: key => values.delete(key)
};

test('reads bounded ranges beyond 2 GB without reading or allocating the whole file', async () => {
    const calls = [];
    const file = { name: 'large.mp4', type: 'video/mp4', size: 256 * 1024 ** 3,
        slice(start, end) {
            calls.push([start, end]);
            return { arrayBuffer: async () => new Uint8Array(end - start).buffer };
        } };
    const [selected] = uploads.capture({ files: [file] });
    const offset = 200 * 1024 ** 3;
    assert.equal((await uploads.read(selected.id, offset, 1024)).length, 1024);
    assert.deepEqual(calls, [[offset, offset + 1024]]);
    await assert.rejects(uploads.read(selected.id, 0, 8 * 1024 ** 2 + 1));
    await assert.rejects(uploads.read(selected.id, file.size, 1));
    await assert.rejects(uploads.read(selected.id, -1, 1));
    await assert.rejects(uploads.read(selected.id, 0.5, 1));
    assert.equal(calls.length, 1);
    uploads.release(selected.id);
    await assert.rejects(uploads.read(selected.id, 0, 1));
});

test('reselecting files preserves earlier file references until explicitly released', async () => {
    const first = new Blob([new Uint8Array([1, 2])]);
    const second = new Blob([new Uint8Array([3, 4])]);
    const [a] = uploads.capture({ files: [first] });
    const [b] = uploads.capture({ files: [second] });
    assert.deepEqual(await uploads.read(a.id, 0, 2), new Uint8Array([1, 2]));
    assert.deepEqual(await uploads.read(b.id, 0, 2), new Uint8Array([3, 4]));
    uploads.release(a.id); uploads.release(b.id);
});

test('recovery records are isolated by organization and conversation', () => {
    uploads.save('org-a', 'chat-a', { name: 'first.mp4' });
    uploads.save('org-a', 'chat-b', { name: 'second.mp4' });
    uploads.save('org-b', 'chat-a', { name: 'third.mp4' });
    assert.equal(uploads.load('org-a', 'chat-a').name, 'first.mp4');
    uploads.forget('org-a', 'chat-a');
    assert.equal(uploads.load('org-a', 'chat-a'), null);
    assert.equal(uploads.load('org-a', 'chat-b').name, 'second.mp4');
    assert.equal(uploads.load('org-b', 'chat-a').name, 'third.mp4');
});

test('oversized selections and recovery records are rejected', () => {
    assert.throws(() => uploads.capture({ files: Array(9).fill(new Blob(['a'])) }));
    assert.throws(() => uploads.save('org', 'chat', { name: 'a'.repeat(4096) }));
    values.set('csweet.media-upload.v1:org:chat', '{broken');
    assert.equal(uploads.load('org', 'chat'), null);
});
