// Plays in-app notification sounds. Audio elements are cached per URL so repeat plays do not re-download.
const players = new Map();
const mutedKey = "csweet.sounds.muted";

// A per-person preference, kept in this browser. Storage can be unavailable (private windows,
// blocked site data); treat that as "not muted" rather than failing playback.
export function isMuted() {
    try { return localStorage.getItem(mutedKey) === "true"; } catch { return false; }
}

export function setMuted(muted) {
    try { if (muted) localStorage.setItem(mutedKey, "true"); else localStorage.removeItem(mutedKey); } catch { /* preference not persisted */ }
}

export function play(url, volume) {
    if (!url || isMuted()) return;
    // A chime is only useful when the user cannot see the thing it announces.
    // If the tab is visible and focused, they are already looking at it.
    if (!document.hidden && document.hasFocus()) return;
    let audio = players.get(url);
    if (!audio) {
        audio = new Audio(url);
        audio.preload = "auto";
        players.set(url, audio);
    }
    audio.volume = Math.min(1, Math.max(0, Number(volume) || 0));
    try { audio.currentTime = 0; } catch { /* not seekable yet */ }
    const result = audio.play();
    // Browsers reject playback until the user has interacted with the page; a missed chime is not an error.
    if (result && typeof result.catch === "function") result.catch(() => { });
}
