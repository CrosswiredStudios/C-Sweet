# Phase 0 · Trace the application and prove the transport

**User story.** As a developer, I need to know which workflows already exist and whether a real phone can carry an encrypted Headquarters session.

**Demonstration.** A reviewer traces a nested web command to its service and sees bounded traffic pass between physical devices and separate Headquarters and relay processes.

## Before starting

Use the .NET SDK and workloads required by the checked-out projects, an Android device, an iPhone with a Mac build/signing host, Windows and Linux Headquarters hosts, and a reachable test relay. Start with `MainPage`, `MauiProgram.CreateMauiApp`, `Routes.GuardNavigationAsync`, `Home`, `Organizations`, `NavMenu` and `SettingsNavigation`. Read the framing limits and recovery rules in the architecture and operations guides.

## Implementation issues, in order

### 1. Inventory one navigation group per issue

Trace each route, linked detail page, dialog, menu, keyboard/drag action and callback. Record its user goal, effective actor, scope, authorization symbol, service, browser dependency and failure case in the matrix. Record aliases against the canonical workflow. Identify local host provisioning separately. Repeat for settings and plugin-contributed actions. A domain reviewer signs off each group against the current source revision.

### 2. Reproduce the package baseline

Read the HQ package pin, cached nuspec repository commit, and matching ConnectionService source. Compare `IConnectionModule` registration with the existing capability probe. Record package ID/version/commit and exact SDKs. Name proposed mobile contracts separately. Preserve the existing provider capability tests.

### 3. Build the disposable transport spike

Use a test relay and native client outside production startup. Model an ordered bounded stream, perform the pinned Headquarters TLS handshake, prove the device key on reconnect, and exchange a known payload in each direction. Force partial reads, backpressure, cancellation, network loss and clean reconnection. Inspect outer traffic for seeded plaintext and observe queue ceilings.

### 4. Prove push delivery

Run PN-01 from the [push guide](push-notifications.md): a closed app on a physical iPhone and Android phone receives a content-free push in a Release build. Same stop rule as the transport spike.

### 5. Freeze the first catalog and evidence

Agree the read, command, receipt, invalidation and error envelopes with the HQ and relay owners. Exercise incompatible majors and missing optional capabilities. Freeze public package contracts only after native stream/key behavior passes on both platforms. Record unresolved compatibility limits and rerun the failed case after any design change.

## Acceptance example

Given two routes using identical test payloads, a phone enrolled to A receives only A responses. Drop a frame during a read. The old channel closes, the caller sees unavailable, and a fresh channel recovers without joining bytes from two sessions.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet --info
dotnet workload list
rg -n '^@page ' src/CSweet.UI/Pages
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter FullyQualifiedName~ConnectionServiceSelectionTests
dotnet test D:/Repositories/CSweet.ConnectionService/tests/CSweet.ConnectionService.Tests/CSweet.ConnectionService.Tests.csproj
```

Attach the source inventory diff, versions, payload and queue measurements, pass/fail by phone/host pair, and redacted disconnect traces to M0. These baseline test commands alone cannot close the physical spike. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
