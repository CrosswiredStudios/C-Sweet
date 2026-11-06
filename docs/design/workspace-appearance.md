# Workspace appearance

`AppearanceThemes` owns the built-in catalog: Night Glass, 41 collection presets, and the optional C-Sweet Daylight palette. `AppearanceState` persists the preset ID under `csweet.appearance`. Night Glass is the default: neutral `#050505`, `#141414`, and `#1b1b1b` surfaces, with a warm selection accent. Navigation has no independent navy palette.

## Sources and synchronization

`Services/AppearanceThemes.json` is the source of truth. Edit it, then run `python scripts/Sync-AppearanceThemes.py` to validate text contrast (primary, muted, warm, danger, ok and accent-2 at 4.5:1, faint text and accent-3 at 3:1, on page, panel and elevated surfaces) and regenerate the stylesheet and ID allowlist. Run with `--check` to detect drift. Generated outputs are committed.

- `Services/AppearanceThemes.json`: embedded catalog with every original token and preset ID.
- `wwwroot/css/presets.css`: resolved preset roles.
- `wwwroot/js/themeCatalog.js`: the same allowlist for pre-paint application.
- `wwwroot/css/theme.css`: geometry, motion, and legacy component role aliases.
- `wwwroot/css/chrome.css`: common menus, tabs, cards, and controls.
- `Layout/NavMenu.razor.css`: rail layout and states, using the selected preset's roles.

`appearance.js` runs before styles paint, applies only a known preset, and migrates legacy appearance IDs. It sets the document root so menus and dialogs rendered by portal providers share the workspace appearance. Every themed layout uses `AppearanceState.MudTheme` and `IsDark`, keeping MudBlazor and the CSS palette aligned. A storage failure still applies the current selection for the session.

## Review

`Pages/Appearance.razor` provides immediate application, faithful preset thumbnails, search, and All/Dark/Light filters. Existing user selections survive reloads; unknown values fall back to Night Glass.

Build/run `tools/CSweet.Promo.Demo` and open `/design-review` for interactive menus, tabs, the board, people cards, and the full appearance picker using real application components with fictional fixture data. `/demo/software/board` and `/demo/software/chat` show the existing product scenes. This demo does not provide the complete application backend.

Verification covers appearance persistence, legacy migration, unavailable storage, a delayed-read race, the complete catalog, every preset round-trip, and agreement between Night Glass's CSS token values and MudBlazor surfaces.
