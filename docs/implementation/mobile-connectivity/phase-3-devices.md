# Phase 3 · Enroll, approve and revoke phones

**User story.** As an administrator, I choose where the relay runs and which new phones require trusted confirmation.

**Demonstration.** Enroll and revoke real phones on official staging and an internal relay, including private recovery with public internet blocked.

## Before starting

Requires the M0 stream proof and Phase 2 membership. Read `IConnectionModule`, `ConnectionServiceSelectionStore.Probe`, `ConnectionServiceSetupStep` and `ManagedConnections`. Prepare separate connector, relay and phone processes, isolated stores, members with different privileges and working internal certificate trust.

## Implementation issues, in order

### 1. Register relay routing and admission (M1)

Implement independent mobile module startup, route-bound connector/phone admission, generation fencing, bounded channel open/frame/close, tested limits, health and a self-host bootstrap. Use two installations and distinct phone keys to test isolation.

### 2. Add Headquarters Mobile access (M2)

Create Disabled/Official/Self-hosted settings and outbound connector. Store protected installation key, exact origin and revision. Render compatibility, reachability, enrollment and active route separately. Verify provider OAuth bindings and local setup survive relay outages.

### 3. Implement private and discovered bootstrap

Implement QR/manual preview and authenticated directory-bound ticket. Verify Headquarters pin and phone key proof. Recheck membership, current role, policy and expiry. Commit activation, audit and relay registration outbox atomically. Recover a lost relay acknowledgement using the original receipt.

### 4. Build trusted approval and device pages

Implement phone-first, privileged-role confirmation and confirm-every-phone modes. Separate awaiting approval from pending relay registration. Limit self-service to own devices and check administrative approvals at HQ. Deny, cancel, expire, rename and revoke through the canonical lifecycle. Revoke stops HQ access immediately.

### 5. Register for push

Build the gateway, relay token store and phone registration (PN-02, PN-03, PN-05, PN-08 in the [push guide](push-notifications.md)). The `security` and `device` categories are delivered in this phase so a waiting phone approval reaches the approver.

### 6. Test replacement and restart

Use local recovery after losing the last trusted phone, revoke it and enroll a replacement. Repeat with internal relay and private identities. Test promotion while waiting, concurrent scans and restart between HQ commit and relay registration.

## Acceptance example

Maya starts as an ordinary member. Promote her before activation under privileged-role confirmation. Enrollment now waits. A trusted approver confirms; drop the acknowledgment. One active grant is recovered. Revoke during relay downtime and verify immediate HQ denial.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test D:/Repositories/CSweet.ConnectionService/tests/CSweet.ConnectionService.Tests/CSweet.ConnectionService.Tests.csproj
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter "FullyQualifiedName~ConnectionServiceSelectionTests|FullyQualifiedName~ConnectionServiceOwnerGatewayTests"
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --filter FullyQualifiedName~MobileEnrollmentTests
```

Attach a transaction/recovery trace per policy, private relay-only installation evidence without provider OAuth credentials, physical phone enrollment and tested replacement instructions. Redact tickets, codes and key material. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
