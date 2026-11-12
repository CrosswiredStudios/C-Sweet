# Phase 5 · Complete everyday workflows

**User story.** As a business member, I can send messages, decide requests and move work forward from my phone, including when a connection drops mid-action.

**Demonstration.** Complete each daily workflow on a real phone and confirm its result at Headquarters under the acting member’s identity.

## Before starting

Requires Phase 4 and current resource authorization. Trace `Communications`, `PersonalAssistant`, `Approvals`, `WorkBoards`, `ProjectSetup`, `Calendar` and `Documents` into their application services. Read the [work management guide](../features/work-management/README.md) and [communications guide](../native-communications-hub.md). Use the order below, taking one workflow row per issue.

## Implementation issues, in order

### 1. Deliver message send with durable receipts (M4)

Persist the original operation key at the phone before submission. Commit the receipt and message at HQ atomically. On uncertain delivery query by original key and reauthorize the result. Repeat for read markers, chat membership, turn controls and assistant settings/chat.

### 2. Implement decisions by type

Fetch current options, revision and authority. Display the enterprise and target on confirmation. Submit once and refresh after stale/decided responses. Test each approval card with its underlying service and test a permission change before making the feature available.

### 3. Complete board and project workflows

Start with project create/membership and item creation/move. Deliver transfer, sprint controls, assignments and stage decisions as individually demonstrated actions. Honor domain transition rules and add accessible alternatives to dragging. Record a real before/after read per action.

### 4. Implement calendar and document slices

Exercise time zones, all-day events, recurrence scope and reminders. Follow Documents into folders, package submission and decisions. Deliver a bounded transfer for the first attachment consumer here and reuse it for Phase 6 imports. Verify cancellation cleans partial content.

### 5. Notify on approvals, messages and attention

PN-04, PN-06 and PN-07 in the [push guide](push-notifications.md): Headquarters decides who to wake from committed outbox changes, the phone shows a localized content-free notification, and a tap opens the right enterprise and list.

### 6. Run the interruption scenarios

Drop responses before and after commit, restart the client and resolve using the original key. Attempt conflicting input and verify a conflict. Remove access before receipt retrieval and ensure the result reveals no protected content. Compare the final domain effect seen from web and phone.

## Acceptance example

A colleague decides revision 7 on the web while it is open on the phone. Submitting those old options returns the current decision and requires a new review where applicable. Separately, losing acknowledgment of a chat message creates exactly one message after recovery.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter "FullyQualifiedName~WorkBoardServiceTests|FullyQualifiedName~ApprovalStateTests|FullyQualifiedName~CommunicationWorkspaceServiceTests|FullyQualifiedName~PersonalAssistantRenderingTests"
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --filter "FullyQualifiedName~CalendarEndpointTests|FullyQualifiedName~MobileOperationRecoveryTests"
```

Attach correlated test receipts, effect counts, denied result reads and per-workflow physical evidence. A pilot release lists the specific workflows it supports. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
