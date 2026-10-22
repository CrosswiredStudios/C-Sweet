# From an idea to play

The finished C-Sweet explainer follows the current Game Studio path: Chief of Staff → Creative & Product → Creative Director → Producer and specialists → planning → Kanban → implementation → technical review → trusted integration → QA → a playable browser game → “What will you build?”

The 112-second film combines actual Aurora app components, the original logo and portraits, cinematic card entrances and camera movement, and an original arrangement of the approved **B · Playful arcade pop** direction at 116 BPM. The game reveal shifts from pale Aurora into a dark arcade palette with glowing bricks, particle bursts, combos and a musical lift. The story is carried by on-screen text; there is no spoken dialogue.

## Watch, play and reuse

| File in `assets/readme/explainer/` | Use |
|---|---|
| [`from-idea-to-play.mp4`](../../assets/readme/explainer/from-idea-to-play.mp4) | 112-second 1920×1080, 30 fps landscape film; H.264 and AAC stereo. |
| [`from-idea-to-play.vertical.mp4`](../../assets/readme/explainer/from-idea-to-play.vertical.mp4) | Separately composed 30-second 1080×1920 social cut with music. |
| [`final-teaser.gif`](../../assets/readme/explainer/final-teaser.gif) | 12-second, 800-pixel-wide silent README invitation. |
| [`final-poster.png`](../../assets/readme/explainer/final-poster.png) | Landscape poster. |
| [`final-storyboard.jpg`](../../assets/readme/explainer/final-storyboard.jpg) | All 11 scene holds. |
| [`final-chapters.vtt`](../../assets/readme/explainer/final-chapters.vtt) / [`transcript.txt`](../../assets/readme/explainer/transcript.txt) | Chapter timing and accessible visual transcript. |
| [`delivery.json`](../../assets/readme/explainer/delivery.json) | Export metadata, measured audio levels and validation results. |

Start the exact-file allowlisted local server:

```powershell
node tools/csweet-explainer/serve.mjs
```

Open [the film player](http://127.0.0.1:5277/tools/csweet-explainer/index.html), then use **Play Prism Break** to try the game. Playback begins on user action. The player has native playback, volume, seek and fullscreen controls, chapter buttons, downloads and a transcript. The game accepts keyboard, pointer and touch input and includes pause and restart.

The README embeds the small GIF and links to the full MP4. A repository MP4 link is not an inline video embed through Markdown image syntax. An inline GitHub video player requires an uploaded GitHub attachment URL; none is fabricated here. The MP4s are ready to upload to a website, video host or social platform. No external publication has been performed.

## Real component provenance

`../CSweet.Promo.Demo/Components/Explainer.razor` hosts the actual `CSweet.UI` components against the in-memory data in `DemoData.Explainer.cs` and `DemoData.FilmSequence.cs`. `FixtureHandler.SendAsync` serves exact typed fixture endpoints and records unmapped requests. The captures use original component styles; `demo.css` only supplies outer layout. `capture-sequence.mjs` records selectors, dimensions, portrait landmarks and SHA-256 hashes in [`captures/sequence-provenance.json`](../../assets/readme/explainer/captures/sequence-provenance.json). Earlier captures are recorded separately in `captures/provenance.json`.

| Story element | Actual component / source |
|---|---|
| Hire review | `CSweet.UI/Components/Hiring/AgentHireDialog.razor`; placement, access and approval. Chief business onboarding is condensed to this real shared hire review. |
| Simplified Overview | `ActivityCard.razor` and `AgentQuestionCard.razor` inside an editorial layout; activity and the Needs your attention focus question. |
| Focus question | `CSweet.UI/Components/AgentQuestionCard.razor`; the exact Game Studio focus prompt and four choices from the Chief's `BusinessOperatingProfiles`. |
| Hiring recommendation | `CSweet.UI/Components/Hiring/HiringSuggestionCarousel.razor`; real suggestion, rationale, proposer and Hire action. Unknown catalog cost stays “Cost unavailable”. |
| Team | Editorial org graphs use original portraits, names and roles; Gabriel reports to Naomi; Adrian, Victor and Rohan report to Gabriel. |
| Brief and prototype handoff | `CSweet.UI/Pages/Communications.razor`; native messages, hiring recommendations and a working local preview URL. |
| Planning | Native Communications exchange between Gabriel and Victor, followed by a hierarchy of native ticket captures that moves into the board. |
| Tickets and sprint | `WorkBoardWorkspace.razor`; PB-04 carries the controls task through implementation, review and QA. One epic, two stories and four tasks reach Done. The completed native cards expand into an editorial gallery, then Naomi’s PB-07 portrait moves into chat. |
| Review ordering | `DeliveryTaskTechnicalReview.ExecuteAsync`, `TaskIntegrationWorkActionExecutor` and `docs/implementation/features/work-management/hierarchical-delivery.md`: candidate review precedes trusted integration and integrated-build QA. |

The cast is Evelyn Brooks, Naomi Chen, Gabriel Reyes, Adrian Silva, Victor Lin and Rohan Desai. These are the current Game Studio roles, using the original bundled agent portraits and `src/CSweet.App/wwwroot/images/icon.svg` and `icon_dark.svg` unchanged.

**This is a condensed illustrative workflow with fictional project data, not a recording of an autonomous agent run.** Models, installed agents, Office readiness and permissions are assumed prepared. Documents, code excerpts and pass labels are sample evidence. Hiring delays and intermediate work are condensed by editorial cuts. Public deployment is separate from browser preview.

`game.mjs` contains the actual Prism Break physics and rendering. `game.html` makes it playable; the film samples the same deterministic simulation. The game was authored for this film and does not establish that a C-Sweet agent delivered it.

## Music and editable source

- `final-film.mjs`: deterministic landscape and portrait compositions, copy, shots and timing. Captures are framed in canvas; UI is not redrawn as invented controls.
- `score-final.py`: original 112-second score derived from the approved B sketch in `music-studies.py`. Bright plucks, syncopated bass, pads, chip melody, synthesized drums and transition effects. No third-party recordings or samples.
- `render-final.mjs` / `render-social.mjs`: headless Chromium frames piped to FFmpeg, H.264/AAC with faststart. Full-range screenshot frames are explicitly converted to limited-range `yuv420p` for consistent browser color.
- `package-final.py`: README GIF, storyboard, metadata, complete decoding and loudness/peak verification.
- `verify-final.mjs` / `verify-game.mjs`: full browser playback, player/game interactions, responsive layouts, serving boundary and deterministic gameplay checks.

The final score is mastered near −16 LUFS with headroom for AAC encoding. The vertical edit follows the same musical direction with the reveal and closing resolution retimed. Raw WAVs and QA frames are under ignored `artifacts/`; finished deliverables, source and native captures are kept in the repository. Use the score and final films together with the repository's applicable licensing; no separate stock-music license is required by this production.

The earlier silent animatic, player (`animatic.html`), score auditions and [`animatic-notes.md`](animatic-notes.md) remain as review history. `render.mjs`, `package.py` and `verify-export.mjs` reproduce that earlier version, not the finished film.

## Reproduce

Requires .NET 10, Node, the existing `tools/csweet-promo` Playwright installation and Chromium, Python with NumPy/SciPy/Pillow, and FFmpeg/FFprobe on PATH. No live account, model provider, backend or agent is contacted.

```powershell
dotnet build tools/CSweet.Promo.Demo -c PromoFilm --no-restore
dotnet run --project tools/CSweet.Promo.Demo -c PromoFilm --no-build --no-launch-profile
# In a second terminal, with the fixture listening on localhost:5198:
node tools/csweet-explainer/capture-sequence.mjs
python tools/csweet-explainer/score-final.py
node tools/csweet-explainer/render-final.mjs --inspect-only
# Inspect artifacts/explainer-final scene holds before encoding:
node tools/csweet-explainer/render-final.mjs
node tools/csweet-explainer/render-social.mjs
python tools/csweet-explainer/package-final.py
node tools/csweet-explainer/verify-game.mjs
node tools/csweet-explainer/verify-final.mjs
```

Set `PLAYWRIGHT_BROWSERS_PATH` to the local Chromium installation if it is outside Playwright's default cache. Rendering starts a temporary loopback server restricted to explicitly listed files and closes it on completion. `serve.mjs` keeps the review player available on port 5277. The demo only changes in-memory fixture data; it does not hire agents or create work in a real company.

README layout checks use `python tools/readme-assets/preview.py` and `node tools/readme-assets/verify.mjs` with the review server running. Evidence is in `artifacts/explainer-final/verification.json` and `artifacts/readme/README-verification.json`. No contract or agent package changes are part of this asset.
