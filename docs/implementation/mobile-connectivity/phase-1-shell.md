# Phase 1 · Build the standalone phone shell

**User story.** As a member of two enterprises, I can launch C-Sweet, choose where to work, and leave an unreachable installation.

**Demonstration.** Walk through fresh install, zero/one/many memberships, private entry, pending approval, Enterprise home and business drill-down with fixtures.

## Before starting

Begin after the Phase 0 startup trace. Inspect `MainPage.xaml`, `MauiProgram`, `MauiSplashScreen`, the mobile `wwwroot/index.html` viewport, `Routes`, `Organizations` and `BusinessSwitcher`. A relay and identity server are unnecessary for this phase. Android builds require its workload; iOS requires the supported Mac/Xcode toolchain.

## Implementation issues, in order

### 1. Replace the MAUI root

Mount a mobile router with explicit local restore, entry, chooser, enrolling, connecting and Enterprise states. Leave web setup and first-administrator guards in the web host. Remove the emulator/localhost default and browser antiforgery client from mobile production startup. Reuse the C-Sweet mark and end splash on completion of local reads.

### 2. Make launch cases deterministic

Define proposed profile store, session coordinator and Headquarters client interfaces. Use separate fixture profiles for zero, one, several, unreachable and pending. Use completion signals to control async state. Register fixtures only in test/development composition. Release startup with no configuration offers sign-in/private entry.

### 3. Implement sign-in and selection

Follow the annotated entry and picker layouts. Wire shared and company sign-in to replaceable launch adapters. Show private invitation preview, camera fallback and expiry. With multiple first-time choices show a chooser; on return show the last enterprise with an immediately available switch.

### 4. Implement the Enterprise and business shells

Reuse business-card presentation, attach authorized shortcuts, and provide the header enterprise selector and drawer business selector. Selecting a card opens that business Overview. Test empty, unauthorized and connection-failed cases independently.

### 5. Implement Back and unsent-edit behavior

Ask before discarding an edit. Reset native and WebView navigation after a switch. Recover focus after dialogs, handle keyboard dismissal, allow text zoom and exercise approval states with fixture inputs. Keep future pilot actions out of navigation until implemented.

## Acceptance example

Maya restores offline Northstar, taps Switch and chooses Harbor. Open Games Overview, press Back and return to Harbor home. Atlas remains a pending invitation. An empty Harbor shows creation only when Maya can create a business.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter "FullyQualifiedName~BusinessNavigationTests|FullyQualifiedName~MobileShellTests"
dotnet build src/CSweet.Maui/CSweet.Maui.csproj -f net10.0-android
# On the iOS build host
dotnet build src/CSweet.Maui/CSweet.Maui.csproj -f net10.0-ios
```

Attach fixture names, route assertions, fresh launch recording, empty/offline screen captures and screen-reader results. Mark evidence fixture-backed; Phase 4 proves live switching. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
