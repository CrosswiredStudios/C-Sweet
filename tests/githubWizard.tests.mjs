import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

const source = await readFile(new URL("../src/CSweet.UI/wwwroot/githubWizard.js", import.meta.url), "utf8");
const wizard = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

test("GitHub setup saves the business draft before submitting and consumes it on return", () => {
    const values = new Map();
    globalThis.sessionStorage = {
        setItem: (key, value) => values.set(key, value),
        getItem: key => values.get(key) ?? null,
        removeItem: key => values.delete(key)
    };
    wizard.submitManifest({ requestSubmit() { assert.equal(values.get("csweet.githubSetupDraft"), "draft-id"); } }, null, "draft-id");
    assert.equal(wizard.takeDraftId(), "draft-id");
    assert.equal(wizard.takeDraftId(), null);
});

test("Starting business setup replaces an abandoned draft return hint", () => {
    const values = new Map();
    globalThis.sessionStorage = {
        setItem: (key, value) => values.set(key, value),
        getItem: key => values.get(key) ?? null,
        removeItem: key => values.delete(key)
    };
    wizard.rememberDraftId("draft-id");
    wizard.submitManifest({ requestSubmit() {} }, "business-id");
    assert.equal(wizard.takeBusinessId(), "business-id");
    assert.equal(wizard.takeDraftId(), null);
    assert.equal(wizard.takeBusinessId(), null);
});
