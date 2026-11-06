// Both layouts and portalled menus use the same document-level appearance.
window.csweetAppearance = {
    apply(theme) {
        const aliases = { night: "night-glass", day: "csweet-daylight", aurora: "csweet-daylight", gold: "signature-gold" };
        const id = aliases[theme] || theme;
        const applied = window.csweetThemeIds.has(id) ? id : "night-glass";
        document.documentElement.dataset.appearance = applied;
        // Browser chrome follows the chosen theme, not the operating-system scheme.
        const paper = window.csweetThemePaper && window.csweetThemePaper[applied];
        if (paper) document.querySelectorAll('meta[name="theme-color"]').forEach(meta => meta.setAttribute("content", paper));
    }
};
try {
    const stored = localStorage.getItem("csweet.appearance");
    const separator = stored?.indexOf("-") ?? -1;
    // Older saved IDs include a collection prefix before the palette ID.
    let theme = stored;
    if (separator > 0 && !window.csweetThemeIds.has(stored)) {
        const palette = stored.slice(separator + 1);
        theme = palette === "gold" ? "signature-gold" : palette;
    }
    window.csweetAppearance.apply(theme);
} catch {
    window.csweetAppearance.apply("night-glass");
}
