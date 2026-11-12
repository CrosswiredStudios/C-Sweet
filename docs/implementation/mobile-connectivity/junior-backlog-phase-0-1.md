# Junior backlog · Phases 0 and 1

Issue-sized work for [Phase 0](phase-0-baseline.md) and [Phase 1](phase-1-shell.md). These two phases do not depend on the open decisions in the [plan review](plan-review-2026-11-11.md), so they can start now. Phases 2–6 get the same treatment after decisions D1–D3 are made.

Type and file names marked *(proposed)* do not exist yet. Everything else was checked against the source on 2026-11-11.

> **Changed by decision D2 (2026-11-11).** Enterprise content is now loaded from Headquarters in a browser view, not rendered from bundled pages. In Phase 1 this changes three issues: **P1-03** renders native-shell states only and, for `Enterprise`, a placeholder that TS-07 later replaces with the browser view; **P1-07** is reduced to the shell's own connecting, unreachable and revoked states (Enterprise home itself comes from Headquarters; its five states move to TS-11); **P1-08** builds the native top bar with the enterprise selector only (the business selector stays in the web app's drawer). P1-04 still applies. See [tunnel and thin shell](tunnel-and-thin-shell.md).

## How to work an issue

1. Copy the issue block into a GitHub issue. Keep the ID in the title.
2. Read the "Read first" files before writing code. If something there contradicts the issue, stop and ask.
3. Write the failing test named in the issue, then the code.
4. Run the commands under "Done when" from the repository root in PowerShell. A filtered `dotnet test` that reports zero tests is a failure, not a pass.
5. Hand off with: what changed, test output, and for anything visual a screenshot labelled **fixture** or **device**.

**Levels.** *Junior* = can be done alone. *Junior + review* = a senior reviews the approach before coding starts. *Senior-led* = a senior owns it; juniors pair. Anything touching keys, TLS, authorization or migrations is never Junior.

**Sizes.** S = up to one day, M = two to three days, L = about a week.

**Rules that apply to every issue**

- Mobile logic that needs tests goes in `src/CSweet.UI/Mobile/` *(proposed folder)*. `tests/CSweet.UnitTests` references `CSweet.UI` and cannot reference `CSweet.Maui`, so code placed in the MAUI project cannot be unit tested. `CSweet.Maui` holds only platform glue (startup, secure storage, camera, browser).
- Every user-visible string goes through `IAppLocalizer` (`src/CSweet.UI/Localization/`). Do not hardcode English in markup.
- Do not change `CSweet.Contracts`, the Agent SDK, Office or WorkManagement packages in these phases. If you think you must, ask first; those packages have version rules in `AGENTS.md`.
- Do not add a NuGet package without it being named in the issue. Versions live in `Directory.Packages.props`.
- No secrets, tokens, QR contents or key material in logs, fixtures, screenshots or test output.

## Setup, once per developer

```powershell
dotnet --info                     # SDK must satisfy global.json (10.0.100, latestFeature)
dotnet workload install maui-android
dotnet build src/CSweet.Maui/CSweet.Maui.csproj -f net10.0-android
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter FullyQualifiedName~BusinessNavigationTests
```

iOS builds need the Mac build host and are run by whoever owns it. If the Android build fails on a clean checkout, record the error in P0-01 before doing anything else.

---

## Phase 0 · Baseline

### P0-01 · Record the working toolchain — S · Junior

- **Goal.** One table of the exact SDK, workloads, Android SDK/emulator image, Xcode and device models the team builds with.
- **Files.** Add `docs/implementation/mobile-connectivity/environment.md` *(proposed)*.
- **Steps.** Run the setup commands. Record versions and any fix needed to get a clean Android build. Ask the Mac owner for the iOS line.
- **Done when.** A second developer follows the file on a clean machine and gets a green Android build without asking questions.

### P0-02 · Generate the route inventory — S · Junior

- **Goal.** A machine-generated list of every web route so the parity matrix can be checked against it.
- **Read first.** [workflow-parity.md](workflow-parity.md), `src/CSweet.UI/Routes.razor`.
- **Files.** Add `scripts/Get-UiRouteInventory.ps1` *(proposed)*; output `docs/implementation/mobile-connectivity/inventory/routes.csv`.
- **Steps.** Find every `@page` directive under `src/CSweet.UI` (there are 70 today). Emit route template, file, and any `[Authorize]` attribute on the page. Sort by route so reruns diff cleanly.
- **Done when.** Row count equals the `@page` count; rerunning with no source change produces no diff.

### P0-03 · Generate the endpoint authorization inventory — M · Junior + review

- **Goal.** Know, for every API endpoint, what authorization it has today. This is the evidence for review finding F1.
- **Read first.** `src/CSweet.Api/Program.cs` (policies and fallback policy, lines 85–115), `src/CSweet.Api/Core/OrganizationUserEndpoints.cs` as an example with no per-caller check.
- **Files.** Add `scripts/Get-ApiEndpointInventory.ps1` *(proposed)*; output `inventory/endpoints.csv`.
- **Steps.** For each `MapGet/MapPost/MapPut/MapPatch/MapDelete` under `src/CSweet.Api` (about 660 in 73 files) record: file, HTTP method, route (including the `MapGroup` prefix), named policy if any, whether the route contains `{organizationId}`, and whether the handler passes the caller's user ID to a service. The last column can be filled by hand for the files a script cannot parse; mark those rows `manual`.
- **Done when.** A reviewer picks ten rows at random and all ten match the source. The summary at the top of the CSV states how many endpoints rely only on the fallback "authenticated user" policy.

### P0-04 · Inventory how pages call the API — S · Junior

- **Goal.** Evidence for decision D2.
- **Files.** Output `inventory/ui-http-usage.csv`.
- **Steps.** List each `.razor` file that injects `HttpClient` directly (84 today) and each typed client registered in `src/CSweet.UI/Services/ServiceCollectionExtensions.cs`. For each, list the URL prefixes it calls. Flag any use of browser-only APIs (`BrowserCredentialsHandler`, `IJSRuntime` file pickers, `window` access).
- **Done when.** Counts match a fresh search, and browser-only usages are listed with file and line.

### P0-05 · Walk one navigation group per issue — M each · Junior + review

Create one issue per group: Overview and Personal Assistant; Communications; Approvals; Projects and Work boards; Calendar and Documents; Employees and Marketplace; Compute and Fleet; Source control; Plugins and connections; Agents; Settings (remaining).

- **Goal.** Confirm or correct the matrix rows for that group.
- **Read first.** The group's rows in [workflow-parity.md](workflow-parity.md), its pages from P0-02.
- **Steps.** Run Headquarters locally. Open every page, dialog, overflow menu and drag action in the group. For each action record: matrix row ID (or "missing"), page file, the API route it calls (from the browser network tab), the service method behind that route, and what happens when the API is unreachable. Add missing rows at the end of the section with the next free ID; never renumber.
- **Done when.** Every control in the group maps to a row, the domain owner has initialled the group in the pull request, and the source revision is written at the top of the notes.

### P0-06 · Reproduce the package baseline — S · Junior

- **Goal.** Confirm the statement in [architecture.md](architecture.md) that Headquarters pins `CSweet.ConnectionService.Protocol` 0.7.0 and that the ConnectionService checkout matches.
- **Steps.** Read the pin in `Directory.Packages.props`. Open the cached package's `.nuspec` and note the repository commit. In the ConnectionService checkout run `git rev-parse HEAD` and read the package version. Run `ConnectionServiceSelectionTests`.
- **Done when.** Package ID, version, nuspec commit and checkout commit are recorded in the M0 row of the [register](acceptance-and-operations.md) and the test run is green with a non-zero test count.

### P0-07 · Transport spike — L · Senior-led

Throwaway code in a separate folder outside the solution (`spikes/mobile-transport/` *(proposed)*). Nothing from it ships. Split into these steps; a junior can own a–b and f with review.

| Step | What to prove | Passing result |
| --- | --- | --- |
| a | A duplex in-memory byte stream with a fixed buffer ceiling | Writer blocks at the ceiling; cancel and dispose unblock both sides |
| b | `SslStream` client and server over (a) in one process, server certificate self-signed, client validates by SPKI SHA-256 pin only | Handshake succeeds with the right pin and fails with a wrong one |
| c | Mutual TLS: server accepts only the enrolled client key | Unknown client certificate is rejected |
| d | Same handshake with the server on Windows using a key loaded from disk, and on Linux | Both pass; record how the Windows key had to be stored |
| e | Bytes carried through a minimal relay hub (`OpenChannel`, `SendFrame`, `CloseChannel`) between two separate processes | Seeded plaintext never appears in relay memory dumps or logs |
| f | Fault injection: dropped frame, duplicate frame, slow reader, network loss mid-read | Channel closes; a new channel starts a new TLS session; no bytes from the old session appear in the new one |
| g | Android physical phone as client | Passes b, c, e, f; record negotiated TLS version |
| h | iPhone as client | Same, plus MessagePack hub protocol works in a Release (AOT) build |

- **Done when.** Each row has pass/fail per host and phone pair in the M0 register row. Any fail stops contract work until the design is revised.

### P0-08 · Threat model — M · Senior-led

- **Files.** Add `docs/implementation/mobile-connectivity/threat-model.md` *(proposed)*.
- **Steps.** One table: asset, attacker (relay operator, network attacker, stolen phone, stolen invitation, malicious Headquarters member, restored backup), what they can do, the control, and the test in the acceptance register that proves it.
- **Done when.** Every "Automated acceptance" row in the register is referenced by at least one threat, and every threat has a test or is marked accepted with a reason.

### P0-09 · Write down the wire and data definitions — M · Senior-led

Blocked by D1–D3. Adds to [architecture.md](architecture.md): entity and index definitions, invitation encoding, inner message envelope, error codes, and one state table per lifecycle (review gaps G5–G7).

---

## Phase 1 · Shell

All Phase 1 work runs against fixtures. No relay, identity server or Headquarters is needed.

### P1-01 · Define the shell contracts — S · Junior + review

- **Read first.** [ui-design.md](ui-design.md) sections "Launch, sign in and choose an enterprise" and "Operate an enterprise"; `src/CSweet.UI/Services/BusinessContext.cs`.
- **Files.** Add under `src/CSweet.UI/Mobile/` *(all proposed)*:
  - `EnterpriseProfile.cs` — record: `ProfileId`, `HeadquartersId`, `DisplayName`, `SecondLine` (for duplicate names), `Kind` (Shared, Private), `MemberLabel`.
  - `IEnterpriseProfileStore.cs` — `ListAsync`, `GetLastActiveAsync`, `SetLastActiveAsync`, `RemoveAsync`.
  - `MobileShellState.cs` — enum: `Restoring`, `Entry`, `Chooser`, `Enrolling`, `PendingApproval`, `Connecting`, `Enterprise`, `Unreachable`, `Revoked`.
  - `IMobileSessionCoordinator.cs` — `State`, `ActiveProfile`, `Generation` (increments on every activation), `StartAsync`, `SelectAsync(profileId)`, `SignOutAsync`, event `Changed`.
- **Steps.** Types only, with XML comments copied from the design. No implementation.
- **Done when.** Solution builds; a senior has approved the names (they will be hard to change later).

### P1-02 · Fixture profile store and coordinator — M · Junior

- **Depends on.** P1-01.
- **Files.** `src/CSweet.UI/Mobile/Fixtures/FixtureProfileStore.cs`, `FixtureScenario.cs` (`Zero`, `One`, `Several`, `Unreachable`, `Pending`), `MobileSessionCoordinator.cs`; tests in `tests/CSweet.UnitTests/MobileShellTests.cs` *(all proposed)*.
- **Steps.** Implement the launch rules from the design: no profiles → `Entry`; one → open it; several on first entry → `Chooser`; a remembered last profile → open it. Use a `TaskCompletionSource` in the fixture so tests control when "loading" finishes; do not use `Task.Delay`.
- **Tests to write.** One per launch rule; `SelectAsync` increments `Generation`; selecting the already-active profile still increments it; a result that completes for an old generation does not change state.
- **Done when.** `dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter FullyQualifiedName~MobileShellTests` runs at least eight tests, all green.

### P1-03 · Mobile root component — M · Junior + review

- **Depends on.** P1-02.
- **Read first.** `src/CSweet.Maui/MainPage.xaml`, `src/CSweet.UI/Routes.razor` (note `GuardNavigationAsync` redirects to `/register` and `/setup`; the phone must never do that).
- **Files.** Add `src/CSweet.UI/Mobile/MobileRoot.razor` *(proposed)*; change `MainPage.xaml` to mount it instead of `ui:Routes`.
- **Steps.** `MobileRoot` renders one view per `MobileShellState`. Only in `Enterprise` does it render the existing router. Leave `Routes.razor` untouched so the web host is unaffected.
- **Done when.** Android build is green; each fixture scenario shows the right first screen (fixture screenshots attached); web app still starts and `BusinessNavigationTests` are green.

### P1-04 · Remove development-only startup from the phone — S · Junior + review

- **Files.** `src/CSweet.Maui/MauiProgram.cs`.
- **Steps.** Delete the hardcoded `ApiBaseUrl` (`10.0.2.2:5149` / `localhost:5149`) and the `AntiforgeryHandler` + cookie `HttpClient` registration from the default composition. Register the fixture store and coordinator only under `#if DEBUG`. In Release with nothing configured the app must land on `Entry`.
- **Done when.** A Release Android build contains no reference to port 5149 (search the built assembly strings) and starts on the sign-in screen.

### P1-05 · Entry screen — M · Junior

- **Depends on.** P1-03.
- **Files.** `src/CSweet.UI/Mobile/EntryView.razor` *(proposed)*; add interface `IMobileSignInLauncher` *(proposed)* with `LaunchSharedSignInAsync`, `LaunchCompanySignInAsync`, `ScanInvitationAsync`, each returning a result type; fixture implementation returns canned results.
- **Steps.** Build the SIGN IN wireframe. Camera permission is requested only after the user taps Scan. Manual entry is always available. Malformed or expired fixture invitations show **Enter another invitation**.
- **Done when.** Tests cover: cancel returns to a usable Entry; denied camera still allows paste; expired invitation message. Fixture screenshots in light and dark theme.

### P1-06 · Enterprise chooser sheet — M · Junior

- **Files.** `src/CSweet.UI/Mobile/EnterpriseChooser.razor` *(proposed)*.
- **Steps.** Build SWITCH ENTERPRISE: search, current row with check mark and accessible label "Current enterprise", second line for duplicate names, Invitations section, Connect private HQ, Manage connections. Pending invitations never auto-select.
- **Done when.** Tests for duplicate-name labelling and that a pending invitation is not selectable as a profile; screen reader reads heading, then rows, then actions.

### P1-07 · Enterprise home with fixture data — M · Junior

- **Read first.** `src/CSweet.UI/Pages/Home.razor`, `src/CSweet.UI/Pages/Organizations.razor`, `src/CSweet.UI/Components/BusinessSwitcher.razor`.
- **Steps.** Reuse the existing business card component. Implement the five distinct states with different copy: loading, no businesses and can create, no businesses and cannot create, connection failed (keeps the enterprise title, offers Retry and Switch), unauthorized.
- **Done when.** One test per state asserting the visible text key; fixture screenshots of all five.

### P1-08 · Header and drawer selectors — M · Junior

- **Read first.** `src/CSweet.UI/Layout/NavMenu.razor`, `src/CSweet.UI/Layout/MainLayout.razor`.
- **Steps.** Enterprise selector in the header on every signed-in screen; focused-business selector in the drawer. Selecting an enterprise always lands on Enterprise home. Do not change the desktop layout: gate by a cascading `IsMobileShell` value *(proposed)* set by `MobileRoot`.
- **Done when.** Web layout tests unchanged and green; new test that selecting an enterprise resets the route to home.

### P1-09 · Unsent-edit guard — M · Junior + review

- **Files.** `src/CSweet.UI/Mobile/IUnsavedWorkRegistry.cs` *(proposed)*.
- **Steps.** Pages with a draft register it. Switching enterprise or business while a draft is registered shows "Switch to Harbor?" with default focus on **Keep working**. Confirming clears drafts and then switches.
- **Done when.** Tests: switch with no draft does not prompt; with a draft prompts; Keep working leaves state and `Generation` unchanged.

### P1-10 · Back behaviour after a switch — M · Junior + review

- **Read first.** `src/CSweet.Maui/Platforms/Android/MainActivity.cs`.
- **Steps.** After an enterprise switch, both the WebView history and the native back stack must be empty, so Back from Enterprise home leaves the app instead of returning to the previous enterprise. Handle Android hardware Back and iOS edge swipe.
- **Done when.** Device recording on Android (and iOS from the Mac owner): Northstar → Games Overview → switch to Harbor → Back exits the app; reopening shows Harbor.

### P1-11 · Viewport and text zoom — S · Junior

- **Files.** `src/CSweet.Maui/wwwroot/index.html`.
- **Steps.** The viewport meta currently has `maximum-scale=1.0, user-scalable=no`. Remove both so users can zoom. Keep `viewport-fit=cover` and add safe-area padding to the shell.
- **Done when.** Pinch zoom works on a device; largest system font size does not clip the header or the primary action on Entry, Chooser and Home.

### P1-12 · Project identity and backup settings — S · Junior + review

- **Files.** `src/CSweet.Maui/CSweet.Maui.csproj`, `Platforms/Android/AndroidManifest.xml`, `Resources/AppIcon`, `Resources/Splash`.
- **Steps.** Replace `com.companyname.csweet.maui` with the identifier the owner chooses (ask; it cannot change after store submission). Replace the template icon, splash and `#512BD4` colour with the C-Sweet mark. Set `android:allowBackup="false"` so device keys added in Phase 3 are never copied to cloud backup.
- **Done when.** Fresh install shows the C-Sweet icon and splash on both platforms; manifest merged output shows `allowBackup=false`.

### P1-13 · Accessibility pass — M · Junior

- **Steps.** With TalkBack (and VoiceOver from the Mac owner) walk Entry, Chooser, Home and the unsent-edit dialog. Check order heading → context → content → primary action, focus returns to the launching control when a sheet closes, and state changes are announced once.
- **Done when.** A short results table per screen and platform is attached to the P1 row of the register, with any failures filed as issues.

### P1-14 · Android build in CI — M · Junior + review

- **Read first.** `.github/workflows/windows-build.yml`.
- **Steps.** Add a workflow that installs the `maui-android` workload and builds `net10.0-android` in Debug on pull requests touching `src/CSweet.Maui` or `src/CSweet.UI`. No signing, no publishing. iOS CI is a separate later issue because it needs a Mac runner and signing decisions.
- **Done when.** The workflow is green on a pull request and red when a deliberate compile error is introduced in `MobileRoot.razor`.

## Phase 1 exit check

Run the acceptance example from [phase-1-shell.md](phase-1-shell.md) with fixtures: Maya restores offline Northstar, switches to Harbor, opens Games Overview, presses Back and lands on Harbor home; Atlas stays a pending invitation; an empty Harbor shows creation only when the fixture says she can create. Record it in the register as **fixture** evidence.
