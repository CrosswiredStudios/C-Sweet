# C-Sweet cinematic promo series

The finished collection is `artifacts/promo/index.html`. It contains six downloadable H.264/AAC MP4s and six posters. Software and YouTube use the existing Aurora Light UI; games use Black & Gold. Landscape films are 1920×1080, 50 seconds, 1,500 frames. Vertical films are separately composed at 1080×1920, 25 seconds, 750 frames. All run at 30 fps. The ending uses the existing logo, “Run the company.” and “What will you build?” with no URL.

## Watch and edit

- `artifacts/promo/exports`: final MP4s and PNG posters.
- `artifacts/promo/storyboards`: six hold contact sheets and three landscape transition sheets.
- `artifacts/promo/captures`: complete app screens, component crops, mobile captures, original portraits/logo and capture provenance.
- `artifacts/promo/audio`: six mastered WAVs, separate atmosphere/pulse/transition stems, raw mixes and `score.json`.
- `artifacts/promo/qa`: full-resolution holds and transition midpoints, decoded export frames, render consistency hashes, file/audio checks and continuous playback results.
- `artifacts/promo/campaign-manifest.json`: exported scenario, timing, copy, source, focal capture, transition and audio information.
- `tools/csweet-promo/manifest.ts`: canonical campaign stories and shot timing.
- `tools/csweet-promo/stage.ts` and `studio.css`: deterministic motion and separate landscape/vertical composition.
- `tools/csweet-promo/audio.py`: original editable score. Fixed seeds generate pads, bells, bass, restrained percussion and transition sounds locally. No licensed recordings, voiceover, or external audio service.
- `tools/CSweet.Promo.Demo/DemoData.cs`: deterministic fictional companies, identities, work, documents and proposals.

The studio at `http://127.0.0.1:5199/` supports campaign/format selection, playback, audio and scrubbing. Space toggles playback; arrow keys step one frame. The finished-file gallery is `/gallery.html`. The static gallery also opens directly from `artifacts/promo/index.html`.

## Reproduce

Requirements: the C-Sweet source checkout, .NET 10 SDK, Node 22.18 or later (tested with Node 24), Python with NumPy and Pillow, and FFmpeg/FFprobe on PATH. The scripts bind only loopback. Chromium is installed once through Playwright. No accounts or product services are required.

From the repository root, keep this running in one terminal:

```powershell
dotnet run --project tools/CSweet.Promo.Demo/CSweet.Promo.Demo.csproj
```

In a second terminal:

```powershell
cd tools/csweet-promo
npm ci
npx playwright install chromium
python -m pip install numpy pillow
npm run preview
```

In a third terminal, from `tools/csweet-promo`:

```powershell
npm run typecheck
npm test
npm run capture
npm run audio
npm run inspect
npm run storyboards
npm run render
npm run assemble
npm run verify
npm run compare
```

Capture and render accept `--campaign=software|youtube|game`; capture also accepts `--scene=...`, and render accepts `--format=landscape|vertical`. For npm commands, pass these after `--`. Rendering uses two bounded workers. It checks a repeated frame after changing scenes before encoding. Each frame is driven by an explicit timestamp; no random or wall-clock CSS animation controls the film. FFmpeg encodes CRF 18 H.264, 4:2:0, stereo 48 kHz AAC at 192 kb/s, with faststart metadata. Its JPEG input retains full-range color signaling (`yuvj420p` when reported by FFprobe).

To reproduce the delivered composition exactly, retain the supplied capture and audio folders and run render, assemble and verify. Re-capturing intentionally uses the current checkout of the app. Product components calculate some calendar headings and relative sprint labels from the host clock, and format dates in the host timezone; those labels may change on a later capture. The supplied October 2026 captures are the fixed rendering baseline. For a future capture, use the calendar’s existing previous/next controls to select October 2026 if needed. No product controls have been rewritten to manufacture a new UI.

Two-pass loudness normalization targets −16 LUFS and a −1 dBTP ceiling. Verification measures the encoded AAC output, rather than assuming the WAV target survived encoding. It accepts ±0.5 LUFS and enforces a peak at or below −1 dBTP. The score deliberately retains headroom; its measured peaks need not touch the ceiling.

## Isolation and fixtures

`Program.cs` references `CSweet.UI` and registers its API clients with a scoped `HttpClient` whose `FixtureHandler` never sends network requests. UI reads resolve to local typed fixtures. Marking a conversation read is the only accepted POST; it changes no persisted state. Every other unsupported method/path returns HTTP 501, prints the unmapped request and records it in `/health`. Capture fails on fixture failures and UI errors. Clicking an unsupported operation produces an error instead of pretending it succeeded.

`DemoHub.SetCommunicationPerspective` supplies the local SignalR connection expected by the existing communications UI. It launches no agents and emits no execution events. No production API, database, Google account or publisher is involved. The wrapper controls stage geometry, uses the existing components/styles/portraits and selects the existing appearance theme. It does not redesign the controls.

The app components are hosted with Interactive Server solely for this demo. Some existing WASM-oriented disposal code can log `JSDisconnectedException` when a browser circuit closes. Such disconnects occur after capture; browser runtime errors during capture, missing assets and unhandled fixture requests remain failures. Both local servers can be stopped with Ctrl+C after viewing.

## Shot provenance and claim boundaries

All fictional records are identified by “Sample data · condensed workflow.” Holds show authored fixture states, not evidence of measured execution speed. Northstar Software, Signal Studio and Orbit Arcade are sample companies. No shipped product, public upload, verified provider result, generated video or generated gameplay is implied.

| Shot / campaigns | Existing implementation and supported behavior | Fixture / capture |
|---|---|---|
| Hook and ending / all | `CSweet.App/wwwroot/images/icon_dark.svg`; `CSweet.UI/wwwroot/images/agents/*-v1.jpg`. Existing brand assets and agent identities. | Original assets copied without editing; no app capability claim. |
| Direction / all | `Pages/Communications.razor`, its `LoadAsync` flow and communications hub/message contracts. User direction and an agent response with a linked draft document. | `DemoData.Hub`, `Messages`; `.hub-main`, `.hub-message.mine`. |
| Plan / all | `Components/Projects/ProjectOverview.razor`, `Brief`, `Metrics` and `RecentActivity`. Project brief, phase, linked board/team and pending readiness information. | `DemoData.Project`; `.project-overview-grid`, `.brief-panel`. Readiness remains pending. |
| Team / all | `Components/Employees/EmployeeDirectoryCard.razor`, `EmployeeViewModel`, `EmployeePortraitScope` and `EmployeePortraits`. Names, roles and matching bundled portraits. | `DemoData.Employees`, `People`, `Installations`; original employee cards. Mobile holds cycle two readable cards at a time for four-role teams. |
| Connected work / software, games | `Components/WorkBoards/WorkBoardWorkspace.razor`, `SelectedSprint` and original work-ticket presentation. Active sprint, status columns, owner and task estimate. | `DemoData.Board`, `Sprints`; `.work-board`, `.work-ticket`. A work card is enlarged and carried into the review sequence. |
| Calendar / YouTube | `Pages/Calendar.razor`, `Load`, `View`, `Days` and `CalendarApiClient.ReadAsync`. Content planning events with owners and work statuses; native Agenda view on mobile. | `DemoData.Calendar`; `.calendar-page`, `.calendar-agenda`. Events are planning/review work, not proof of public publishing. |
| Comment review / YouTube | `Components/ConnectorActionApprovalCard.razor`, `Card`, `Decide`, `ResourceChangeDecisionKinds`. Exact proposed reply, feedback and approve/revise/reject controls. | `DemoData.CommentApproval`; pending `.mud-paper`. The film shows review, never a provider-completed result. |
| Document review / software, games | `Components/ArtifactWorkspace.razor`, `LoadAsync`, `LatestRevision` and its markdown preview. Open document content, revision and review affordances. | `DemoData.Document`; `.artifact-workspace`, `.artifact-preview-shell`. Draft first milestone and game creative brief; no generated gameplay. |
| Channel report / YouTube | `Components/ArtifactWorkspace.razor` document viewer. Reporting behavior is documented in `docs/implementation/youtube-manager.md` (reporting duty and analytics coverage). | Fictional metrics in `DemoData.Document` explicitly labeled sample data. It depicts inspecting a report, not a live analytics fetch, growth guarantee or publication. |
| Evidence / software, games | `Components/Projects/ProjectResourceSection.razor`, `Resources` and its revision/status rendering. Documents and checks support a review decision. | `DemoData.Evidence`; `.resource-section`. Records remain Pending, InReview or Draft. |

Landscape chapter boundaries are 0/3/10/18/31/44/50 seconds; vertical boundaries are 0/2/6/10/16/21/25 seconds. Scenes within those chapters use 0.72-second transitions. Source-message, employee and ticket crops act as anchors while the actual next app surface reveals. Perspective, masked reveals, typography and layered backgrounds are compositor treatments outside the app controls. Vertical shots use mobile cards, native calendar agenda and selected document sections, with important copy inset from the top, sides and bottom overlay areas.

## Verification

`timeline.test.ts` checks every frame for continuous shot coverage, exact durations, settled holds and the YouTube claim boundaries. `inspect.ts` checks all six compositions for loading/runtime failures, captures all holds and transition midpoints, and rejects overlapping closing logos/copy. `render.ts` records repeated-frame SHA-256 checks and zero browser errors. `verify.ts` verifies dimensions, duration, frame count/rate, codecs, faststart, complete decode, absence of black intervals, AAC loudness and true peak; it also exports decoded holds/transitions and plays every complete MP4 continuously with audio enabled and muted in Chromium. Playback statistics are saved, including dropped frames and startup waits. `compare_frames.py` compares every pixel of all 96 decoded holds/transition midpoints with frame-aligned compositor captures and creates 12 encoded contact sheets. The compositor caches decoded asset dimensions so scrubbing to a newly created scene cannot change its pan after loading. Those automated audio checks verify transport and levels; listening quality is an editorial judgment.

Delivered checks: 13 timeline tests passed; TypeScript and the demo host built successfully. All six files passed media/decode/loudness checks and both complete playback modes, with zero dropped frames. Encoded audio measured −16.00 to −16.02 LUFS and −4.99 to −5.17 dBTP. All 96 visual comparisons passed with a maximum full-resolution RMSE of 2.931 on the 0–255 scale. Fixture failures, browser render errors, decode errors and black intervals were zero. The browser recorded one startup buffering event per playback, with no dropped frames.

The final checks are recorded under `artifacts/promo/qa`. Full-resolution holds and contact sheets support visual review at desktop and phone sizes. Generated artifacts are ignored by Git; copy `artifacts/promo` when archiving or moving the finished series. Source code changes remain under the two tools directories and do not require contract or agent-package version changes. Publishing is outside this implementation.
