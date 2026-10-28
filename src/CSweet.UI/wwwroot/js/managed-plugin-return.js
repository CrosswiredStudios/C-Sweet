export function consumeReturn() {
    const fragment = window.location.hash.slice(1);
    window.history.replaceState(null, "", window.location.pathname);
    const values = new URLSearchParams(fragment);
    const handoff = values.get("handoff");
    const attemptId = values.get("attempt");
    if ([...values.keys()].length !== 2 || !/^[A-Za-z0-9_-]{43}$/.test(handoff || "") ||
        !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(attemptId || "")) {
        throw new Error("Invalid connection return");
    }
    return { handoff, attemptId };
}
