// A textarea normalizes CRLF and lone CR to LF. Return offsets in the original
// UTF-16 source so the server publishes exactly the selected source characters.
export function selection(element, source) {
    if (!element || element.value !== source.replace(/\r\n?/g, "\n")) return [-1, 0];
    const start = element.selectionStart, end = element.selectionEnd;
    if (end <= start) return [-1, 0];
    let browserOffset = 0, originalStart = -1, originalEnd = -1;
    for (let offset = 0; offset <= source.length; offset++) {
        if (browserOffset === start && originalStart < 0) originalStart = offset;
        if (browserOffset === end) { originalEnd = offset; break; }
        if (source[offset] === "\r" && source[offset + 1] === "\n") offset++;
        browserOffset++;
    }
    return [originalStart, originalEnd - originalStart];
}
