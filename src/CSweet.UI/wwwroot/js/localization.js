const LANGUAGE_KEY = 'csweet.language';
const TIMEZONE_KEY = 'csweet.timezone';

function get(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

export function read() {
    let deviceTimeZone = null;
    try { deviceTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone ?? null; } catch { /* unsupported */ }
    return {
        storedLanguage: get(LANGUAGE_KEY),
        storedTimeZone: get(TIMEZONE_KEY),
        deviceLanguage: navigator.language ?? (navigator.languages && navigator.languages[0]) ?? null,
        deviceTimeZone
    };
}

export function store(kind, value) {
    const key = kind === 'timezone' ? TIMEZONE_KEY : LANGUAGE_KEY;
    try {
        if (value) localStorage.setItem(key, value); else localStorage.removeItem(key);
    } catch {
        throw new Error('storage unavailable');
    }
}

export function applyDocumentCulture(lang, dir) {
    const root = document.documentElement;
    root.lang = lang;
    root.dir = dir;
}
