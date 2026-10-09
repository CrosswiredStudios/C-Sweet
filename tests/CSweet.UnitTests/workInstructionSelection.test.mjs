import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const sourcePath = new URL('../../src/CSweet.UI/wwwroot/js/workInstruction.js', import.meta.url);
const { selection } = await import(`data:text/javascript;base64,${Buffer.from(await readFile(sourcePath)).toString('base64')}`);

test('selected instruction preserves original CRLF UTF-16 positions and emoji', () => {
    const source = 'Private 😀\r\nMake it blue.\r\nPersonal.';
    const value = source.replace(/\r\n?/g, '\n');
    const start = value.indexOf('Make');
    const range = selection({ value, selectionStart: start, selectionEnd: start + 13 }, source);
    assert.deepEqual(range, [source.indexOf('Make'), 13]);
    assert.equal(source.substring(range[0], range[0] + range[1]), 'Make it blue.');
});
test('selection spanning normalized CRLF retains source line endings', () => {
    const source = 'Start\r\nMiddle\rEnd';
    assert.deepEqual(selection({ value: 'Start\nMiddle\nEnd', selectionStart: 5, selectionEnd: 16 }, source), [5, 12]);
});
test('empty selection and a changed source are rejected', () => {
    assert.deepEqual(selection({ value: 'Original', selectionStart: 0, selectionEnd: 0 }, 'Original'), [-1, 0]);
    assert.deepEqual(selection({ value: 'Changed', selectionStart: 0, selectionEnd: 7 }, 'Original'), [-1, 0]);
});
