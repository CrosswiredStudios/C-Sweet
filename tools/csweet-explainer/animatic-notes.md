# From an idea to play — animatic 01

This is the **story and pacing review**, before final production. The silent 96-second film follows the current Game Studio path: Chief of Staff → Creative & Product → Creative Director → Producer and specialists → scope and sprint → implementation → independent technical review → trusted integration → QA → accepted browser preview → “What will you build?”

The intended feeling is possibility becoming tangible. The owner supplies one sentence; each handoff gives that sentence more structure until it becomes play. Aurora lavender, blush and teal surround the original C-Sweet logo and portraits. The game carries those colors into the payoff. Large type and focused cards keep the story legible without narration.

## Review

- `assets/readme/explainer/from-idea-to-play.animatic.mp4`: 1280×720, 24 fps, H.264, silent, 96 seconds. Full-range JPEG frames are converted to explicitly signaled limited-range 4:2:0 (`yuv420p`) for consistent browser playback.
- `assets/readme/explainer/teaser.gif`: short README invitation; links to the complete MP4.
- `assets/readme/explainer/storyboard.jpg`: all 13 scene holds.
- `assets/readme/explainer/chapters.vtt`: chapter titles and timecodes.
- `tools/csweet-explainer/index.html`: interactive review player with play/pause, seek, chapter selection and fullscreen.
- `tools/csweet-explainer/film.mjs`: editable story, descriptions, timing and deterministic drawing.

Open the MP4 directly, or start the review server. It serves only an explicit allowlist of review files and required brand assets, with no directory browsing:

```powershell
node tools/csweet-explainer/serve.mjs
```

Visit `http://127.0.0.1:5277/tools/csweet-explainer/index.html`. Playback starts only when requested. Space toggles playback; left/right arrows seek one second outside focused controls. The player includes a text description of every scene. The MP4 carries the essential story as on-screen copy; there is no audio-only information.

## Music direction review

Three short original synthesized sketches are available in `assets/readme/explainer/music/`. They audition mood, tempo and instrumentation before the full score is arranged. They contain no vocals, licensed recordings or third-party samples and have not been added to the film. Each follows an eight-bar idea with a short closing resolution; durations differ with tempo. Encoded loudness is matched near −18 LUFS for comparison.

| Study | Direction | Tempo | Duration |
|---|---|---|---|
| A · `a-warm-electronica.mp3` | Warm cinematic electronica: soft keys, pads, rounded bass and a restrained beat. Recommended starting point for C-Sweet. | 100 BPM | 21.5s |
| B · `b-playful-arcade.mp3` | Playful arcade pop: bright plucks, syncopated bass and a little chiptune. A more game-focused identity. | 116 BPM | 18.85s |
| C · `c-cinematic-lift.mp3` | Cinematic lift: piano-like keys, swelling synth strings and broader percussion. More expansive and emotional. | 88 BPM | 24.12s |

Reproduce with `python tools/csweet-explainer/music-studies.py` (NumPy, SciPy and FFmpeg). Raw WAVs are in ignored `artifacts/explainer-review/music`; compact MP3 auditions and their measured loudness/peak metadata are in `assets/readme/explainer/music`. These are arrangement sketches, not a final acoustic recording or final soundtrack production. The score awaits music-direction feedback.

## Story and proposed narration

| Time | Visual action | Proposed voiceover for final production |
|---|---|---|
| 00–05 | A sentence becomes six portraits. | Every game starts with a little “what if.” |
| 05–12 | Evelyn’s profile, review access, approve and hire. | Start with a Chief of Staff who helps turn your direction into a company. |
| 12–20 | Exact focus question; Creative & Product selected and sent. | You choose where to begin. |
| 20–27 | Owner asks for a one-minute browser arcade game. | A small game. A clear ambition. Something worth playing again. |
| 27–35 | Hiring suggestion → Naomi’s hire → creative ownership. | Bring in a Creative Director to give the idea its shape. |
| 35–43 | Producer, Engineer, Technical Director and QA join. | Then assemble the people who will carry it forward. |
| 43–51 | Approved pitch becomes epic, story, tasks; sprint activation. | Together, they turn the vision into a plan, with a next step for every person. |
| 51–59 | PB-03 moves from Ready to In Progress. | Follow the work as it moves. |
| 59–66 | Adrian implements, checks and publishes a candidate. | Your engineer builds. |
| 66–73 | Victor reviews the candidate; trusted integration follows. | Your technical lead reviews what moves forward. |
| 73–80 | Rohan verifies the integrated build; aggregate acceptance. | QA checks the experience. The team reviews the whole. |
| 80–89 | Prism Break animates inside a browser preview. | And an idea becomes something you can play. |
| 89–96 | Brand lockup and final question. | Your idea. Your company. Your workforce. What will you build? |

## Product fidelity

The user selected the current **game-studio team** for this story. Roles and names come from current sibling manifests: Evelyn Brooks, Naomi Chen, Gabriel Reyes, Adrian Silva, Victor Lin and Rohan Desai. Software Product Manager and Software Architect are not substituted into this path.

| Depiction | Source of truth for the polished capture |
|---|---|
| Chief and other hire reviews | `CSweet.UI/Components/Hiring/AgentHireDialog.razor`: `profile-identity`, placement, model, Review Access, Approve and Hire. Chief onboarding uses the business onboarding flow; the generic profile is a simplified review representation here. |
| Focus selection | Chief repo `BusinessOperatingProfiles` Game Studio options; `ChiefOfStaffAgent.AttachOnboardingFocusDecisionAsync` creates “Where would you like the company to focus first?”; `CSweet.UI/Components/AgentQuestionCard.razor` renders the decision. |
| Hiring suggestions | `CSweet.UI/Components/Hiring/HiringSuggestionCarousel.razor`: heading, status, portrait, role, rationale, proposer and Hire. The animatic omits price because no price fixture has been verified. |
| Ownership | Chief repo `ChiefOfStaffProfile` and `BusinessOperatingProfiles`: Creative Director owns game vision and team design; Producer reports to Creative Director. |
| Planning and board | `CSweet.UI/Components/WorkBoards/WorkBoardWorkspace.razor`; `docs/implementation/features/work-management/hierarchical-delivery.md`. Final board columns should use the installed workflow’s exact labels. |
| Implementation and independent review | `DeliveryTaskTechnicalReview.ExecuteAsync`, `TaskIntegrationWorkActionExecutor`; exact candidate review precedes trusted story integration and integrated-commit QA. |
| Running browser preview | `DeliveryComputeTest` and `ProjectReleases` in hierarchical-delivery documentation; preview is not public deployment. |

These are simplified illustrations for editorial review, **not actual component captures**. The original portrait and logo files are loaded unchanged. The existing README screenshots remain unchanged. Sample company, pitch, ticket IDs, code, pass labels and gameplay are authored fiction. Time is condensed; no speed, successful autonomous run, measured test result or published game is claimed. Office, models, packages and permissions are assumed ready before the story begins. A single representative ticket illustrates a larger project, and the remaining scope is condensed before the final acceptance.

## Final production direction after review

1. Confirm story order, pacing, browser-game concept and closing language with the owner.
2. Extend the isolated `CSweet.Promo.Demo` fixtures for the exact onboarding decision, hiring plan, hire review, planning hierarchy, board progression and candidate review. Capture the real Razor components in Aurora, using the original portraits. Do not alter product controls to manufacture a result.
3. Replace the illustrated cards with those captures. Keep the PB-03 ticket as the visual connection through author, technical reviewer and QA. Make each actual click and confirmation readable. Condense package preparation and hiring waits with an explicit editorial cut.
4. Produce a 1920×1080 / 30 fps master, a separately composed 1080×1920 social cut and a small README GIF linked to the full film. The vertical cut must reframe cards rather than crop the landscape video.
5. Use a restrained original music bed: curiosity in the opening, a pulse as the team assembles, momentum on the board, a brief breath at review, then a lift into gameplay. Optional narration uses the script above; deliver captions, music-only and silent variants. No licensed stock track or synthetic voice is included in this review.
6. Keep the source/provenance distinction explicit. Either retain a labeled concept game or capture an actual reviewed app run for the final payoff; do not present this authored game animation as evidence of agent output.
7. Inspect holds and transitions, text at README width, the complete encoded film and sound, and the separately composed mobile cut before marking the asset final.

## Reproduce and inspect

Requires Node 22.18+, FFmpeg and FFprobe on PATH, Python with Pillow, and the existing promo tool’s pinned Playwright/Chromium. No provider, account, backend or agent is contacted.

```powershell
npm ci --prefix tools/csweet-promo
# If Chromium is not already installed:
cd tools/csweet-promo
npx playwright install chromium
cd ../..
node tools/csweet-explainer/render.mjs --inspect-only
node tools/csweet-explainer/render.mjs
python tools/csweet-explainer/package.py
node tools/csweet-explainer/verify-export.mjs
```

`render.mjs` starts an ephemeral loopback server, checks playback/seek/chapter controls and mobile overflow, verifies a repeated frame after seeking across scenes, captures scene holds and encodes the film. `package.py` makes the storyboard and README teaser, probes the encoded stream and requires a complete decode without errors. QA evidence goes to ignored `artifacts/explainer-review`; deliverables live in tracked `assets/readme/explainer` so README links survive a clean checkout. The render server and browser close when the command exits. No contract or agent package is changed.

`verify-export.mjs` checks that unrelated repository paths return 404, plays the complete encoded MP4 in Chromium at 2× speed and records playback metadata and dropped frames. The existing `tools/readme-assets/preview.py` and `verify.mjs` check the README embeds and local links at desktop/mobile sizes in light/dark themes using the allowlisted server on port 5277.

October 22 review verification: all 13 holds inspected; repeated-frame hashes matched; player controls and mobile overflow checks passed; 2,304 encoded frames decoded with no errors; complete browser playback was checked at 2× speed; all four README theme/viewport combinations passed link, image and overflow checks. The encoded film uses explicit limited-range color after browser visual inspection. Current sizes and playback frame statistics are in `artifacts/explainer-review/verification.json`. This validates the review deliverable, not a live agent workflow.
