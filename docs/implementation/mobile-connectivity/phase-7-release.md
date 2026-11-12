# Phase 7 · Prove, release and operate the product

**User story.** As an operator, I can install released components, operate privately and recover service without restoring access to a revoked phone.

**Demonstration.** Run clean install, upgrade, outage, restore, relay migration and rollback with physical phones, and record the evidence for general availability.

## Before starting

Requires Phase 6 workflow evidence. Use production-shaped artifacts for MAUI, Headquarters, Identity, directory, Market, relay and protocol. Prepare Windows/Linux Headquarters, physical Android/iOS phones, official staging and an isolated internal network. See the [operations register](acceptance-and-operations.md) for topology, M5/M6 milestones and capacity tests.

## Implementation issues, in order

### 1. Verify compatibility and installation

Record component versions and negotiated features. Install from packaged artifacts. Test fresh/upgrade, mobile-disabled default, absent mobile module, missing optional capability and unsupported major. Verify local web and existing provider OAuth still function.

### 2. Run official and isolated drills

On staging test carrier NAT, network changes, directory outage and quota recovery. Block external DNS/HTTPS from first launch in private testing. Invite, authenticate, approve, work, revoke and replace a device with local users and then a company IdP. Capture network observations and private deployment prerequisites.

### 3. Rehearse loss, restore and migration

Lose and revoke a phone. Restore HQ only through the mobile-disabled rotation procedure. Reconcile relay grants after relay restore. Move to another relay with an offline phone. Check old generations remain denied and uncertain operations still resolve. Document the actual operator steps tested.

### 4. Measure usability and capacity

Validate TalkBack/VoiceOver, large text, keyboard, safe areas and narrow widths. Run load, soak and reconnect storms from the register. Reassess per-HQ phone limits, memory and egress with multi-user and actual transfer workloads. Record operating ceilings and alerts.

### 5. Prepare store submission

Final application identifier, icons and splash; Android backup disabled; Apple privacy and encryption declarations; push entitlements and production push credentials at the gateway; in-app C-Sweet account deletion (required once the app can create accounts); supported-version window between app, Headquarters and relay with an update-needed screen.

### 6. Record the general-release decision

Verify physical results for all matrix rows on both phones, host/relay combinations, private isolation and independent artifact upgrades. Name any partial pilot explicitly. Rehearse disabling new sessions without losing receipts or revocations. Attach approved release identifiers when a release actually occurs.

## Acceptance example

Restore a disposable HQ after one phone was revoked and a different phone lost a command acknowledgment. Follow local operator recovery and enroll a replacement. The lost phone stays denied; the recovered domain result shows a single effect.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj -c Release
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj -c Release
dotnet test D:/Repositories/CSweet.ConnectionService/CSweet.ConnectionService.slnx -c Release
dotnet test ../CSweet.Market/CSweet.Market.slnx -c Release
dotnet build src/CSweet.Maui/CSweet.Maui.csproj -c Release -f net10.0-android
# On the iOS build host
dotnet build src/CSweet.Maui/CSweet.Maui.csproj -c Release -f net10.0-ios
```

Attach release compatibility and device results, redacted load/restore measurements, published artifact metadata and rollback rehearsal. Keep pilot M6 and general-release P7 decisions distinct. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
