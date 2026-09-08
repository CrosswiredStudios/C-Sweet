const files = new Map();
const maximum = 8 * 1024 * 1024;

export function capture(input) {
    const selected = Array.from(input.files ?? []);
    if (selected.length > 8) throw new Error("Select no more than eight files.");
    const result = selected.map(file => {
        const id = crypto.randomUUID();
        files.set(id, file);
        return { id, name: file.name, contentType: file.type, size: file.size };
    });
    input.value = ""; // Allow selecting the same file again after a validation error or pause.
    return result;
}

export async function read(id, offset, count) {
    const file = files.get(id);
    if (!file || !Number.isSafeInteger(offset) || !Number.isSafeInteger(count) ||
        offset < 0 || count < 1 || count > maximum || offset + count > file.size)
        throw new Error("The selected file range is unavailable.");
    return new Uint8Array(await file.slice(offset, offset + count).arrayBuffer());
}

export function release(id) { files.delete(id); }
const key = (org, chat) => `csweet.media-upload.v1:${org}:${chat}`;
export function load(org, chat) {
    const value = sessionStorage.getItem(key(org, chat));
    if (!value || value.length > 4096) return null;
    try { return JSON.parse(value); } catch { return null; }
}
export function save(org, chat, ticket) {
    const json = JSON.stringify(ticket);
    if (json.length > 4096) throw new Error("Upload recovery metadata exceeds its limit.");
    sessionStorage.setItem(key(org, chat), json);
}
export function forget(org, chat) { sessionStorage.removeItem(key(org, chat)); }
