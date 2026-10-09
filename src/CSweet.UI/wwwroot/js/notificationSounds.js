// Plays in-app notification sounds. Audio elements are cached per URL so repeat plays do not re-download.
const players = new Map();

export function play(url, volume) {
    if (!url) return;
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
