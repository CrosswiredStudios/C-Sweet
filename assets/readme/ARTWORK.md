# README artwork

## Animated explainer (October 22, 2026)

`explainer/` contains the finished 112-second 1920×1080 film, a separately composed 30-second 1080×1920 marketing cut, a 12-second README GIF, poster, chapter track, transcript and storyboard. Both MP4s have original stereo music. The README embeds `final-teaser.gif` and links to `from-idea-to-play.mp4`. Source, reproduction instructions and provenance are in [`tools/csweet-explainer`](../../tools/csweet-explainer/README.md).

The film uses actual Razor components from `CSweet.UI`, captured through the in-memory `CSweet.Promo.Demo` fixture. `explainer/captures/sequence-provenance.json` records the revised sequence’s selectors, dimensions, portrait landmarks and capture hashes; `provenance.json` retains earlier capture provenance. Component styles, the original `icon.svg` and `icon_dark.svg` and agent portraits remain unchanged; editorial typography, camera movement and framing sit outside the product captures. The approved story follows Chief of Staff, Creative Director, Producer, Engineer, Technical Director and QA. Prism Break is an authored playable browser game using the same renderer and physics in the film and demo page; it is illustrative output, not a captured agent run. Music direction B is original synthesized arcade pop at 116 BPM, with no third-party recordings or samples. `delivery.json` records encoded dimensions, audio, duration and measured loudness. Earlier animatic and music studies are retained as source history. No media has been published externally.

## Aurora presentation (October 14, 2026)

The current README uses actual app components and editable vector diagrams. It does not use a generated product mockup. The palette follows `src/CSweet.UI/wwwroot/css/app.css`'s Aurora tokens: plum text `#30263f`, muted text `#72667d`, accent `#735090`, raised paper `#fbfaff`, lavender `#c5c1f2`, blush `#eac2dd`, and soft teal `#c6e8ea`.

| Asset | Content |
|---|---|
| `csweet-aurora-hero.png` / `csweet-aurora-hero-mobile.png` | Original C-Sweet logo and portraits; a native conversation-message crop, sprint board, and assigned work card. Separate desktop/mobile compositions. |
| `csweet-aurora-workspace.png` / `csweet-aurora-workspace-mobile.png` | Native employee cards, assigned tickets, and document revision/markdown preview. Mobile uses focused employee-card captures and stacked panels. |
| `csweet-company-workflow.svg` / `csweet-company-workflow-mobile.svg` | Direction → Plan → Team → Work → Review → Company memory. A learning loop closes the desktop path; mobile presents six readable steps. |
| `csweet-security.svg` / `csweet-security-mobile.svg` | High-level authority and execution boundaries: user, Headquarters, and separately installed Office. Docker is identified as trusted development infrastructure. |

All screenshots use fictional Northstar Software fixtures from `tools/CSweet.Promo.Demo/DemoData.cs`. Capture runs never connect to real agents, databases, accounts, or external services. Product controls, text, names, statuses and portraits remain the app's rendered output. Decorative frames and typography sit outside those captures.

The captured components are `Pages/Communications.razor`, `Components/Employees/EmployeeDirectoryCard.razor`, `Components/WorkBoards/WorkBoardWorkspace.razor`, and `Components/ArtifactWorkspace.razor`. Inputs are retained under `screenshots/`; `capture-sources.json` records native selectors, regions, scale, and zero fixture/runtime failures. `provenance.json` records SHA-256 hashes. Dates and statuses shown are sample records.

### Edit and reproduce

`tools/readme-assets/build.py` is the editable artwork source: diagram labels/geometry, Aurora palette, and image composition. It can rebuild from the retained screenshots after generated artifacts are cleaned. `capture.mjs` refreshes the inputs at 3× resolution; it uses the existing Playwright dependency from `tools/csweet-promo`.

To refresh app inputs, run the isolated demo in its own terminal:

```powershell
dotnet run --project tools/CSweet.Promo.Demo -c ReadmeAssets
```

Install the capture runtime once if it is not already present:

```powershell
npm ci --prefix tools/csweet-promo
python -m pip install markdown-it-py
cd tools/csweet-promo
npx playwright install chromium
cd ../..
```

From the repository root, run:

```powershell
# Optional: refresh the retained sample app captures.
node tools/readme-assets/capture.mjs

python tools/readme-assets/build.py
python tools/readme-assets/preview.py
```

Keep this loopback-only server running in another terminal:

```powershell
python -m http.server 5277 --bind 127.0.0.1
```

Then render and verify from the repository root:

```powershell
node tools/readme-assets/render.mjs
node tools/readme-assets/verify.mjs
```

Python needs `markdown-it-py` for `preview.py`; the diagram/composition builder uses the standard library. Playwright renders images and verifies fonts, image decoding, text bounds and composition spacing. README verification checks local link targets, fragment anchors, and light/dark desktop/mobile rendering. Generated previews and QA records go under `artifacts/readme`; published assets stay under `assets/readme`.

The SVGs are self-contained: no script, remote image, or external font dependency. They contain title/description metadata, and the README repeats their key information as text. `<picture>` selects the separately composed mobile versions at 600 CSS pixels or below. PNG compositions load the original logo and agent portraits directly from the app's source assets.

## Archived founder illustration

`csweet-founder-hero.png` is a conceptual marketing illustration, not a product screenshot. Generated with the built-in imagegen tool on September 6, 2026. The original hero and sign-in screenshot are preserved alongside it.

### Generation prompt

Create a premium editorial marketing hero illustration for C-Sweet, a self-hostable operating environment for agent-first companies. Use-case: ads-marketing. Wide landscape 1536x1024 or wider composition suitable for the top of a GitHub README. Show one confident founder seated in an elegant minimalist command studio, directing a clearly organized company represented by a few connected architectural workspaces: planning, software creation, creative production, and review. Make the founder central and the connected departments feel purposeful, calm, and capable. Sophisticated semi-realistic 3D editorial illustration, architectural precision, tactile materials, soft cinematic daylight, warm ivory environment with deep navy and restrained emerald green accents, matching a premium technology brand. Spacious balanced composition, generous negative space, a single strong focal point, clean connecting paths and small organized work artifacts. Human scale and agency; no robot army, no sci-fi spaceship, no neon overload, no crowded tiny icons. The illustration must look like a conceptual illustration, not a screenshot or a real application interface. No text, no typography, no lettering, no logos, no watermarks. Rounded geometric platforms and subtle green illuminated connections can communicate work flowing through an organization. Background fully opaque and self-contained so it looks equally good embedded on a light or dark GitHub page. Polished, ambitious, memorable, visually clear at small width.

## Earlier diagram direction

The earlier workflow and security diagrams used navy, ivory and green. Their filenames are retained by the new Aurora diagrams so existing references continue to work. The original founder illustration, `csweet-hero.webp`, and `csweet-login.png` remain archived; the current README does not present them as current UI captures.
