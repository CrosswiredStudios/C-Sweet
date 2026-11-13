# Phase 4 · Open a live enterprise and switch safely

**User story.** As Maya, I can work in Games at Northstar, switch to Games at Harbor and know which enterprise will receive my next action.

**Demonstration.** On both phone platforms connect to two live installations, open a real business Overview, switch mid-read, use Back, suspend and relaunch.

## Detailed guide

Each enterprise's pages load from its own Headquarters into a per-enterprise browser view ([tunnel and thin shell](tunnel-and-thin-shell.md)). Where the issues below say "compose an enterprise-scoped client" or "bind reads", that means: activate that profile's view and bridge, mark the routes it needs, and deliver realtime hints (TS-07). Switching enterprise switches views; one view's results can never reach another. If the prototype failed, the bundled fallback applies.

## Before starting

Requires Phases 1–3. Inspect `Home`, `Organizations`, `BusinessContext`, `AppRealtimeState`, `BusinessSwitcher`, `NavMenu` and `ApplicationRealtimeOutboxDispatcher`. Prepare Northstar and Harbor, each with Games, the same display email and different membership grants. Give only one installation an unread conversation.

## Implementation issues, in order

### 1. Resolve profiles and open the selected enterprise

Join directory entries with local profiles by stable HQ/member binding. Keep invitations separate. Implement last/sole/multiple choices, stale directory removal and duplicate-name labels. Report the selected installation as unreachable if its connector is offline; let users choose another.

### 2. Bind real home and business reads

Compose an enterprise-scoped client and authorization-aware home model. Source business cards, shortcuts, Overview, counts and permissions from Headquarters. Map loading, no businesses, no create permission, offline and revoked to the distinct design states. Store focus only within the active profile.

### 3. Implement switch as a single session transition

Start with the failure: hold Northstar’s list request while Maya switches to Harbor. Invalidate Northstar’s activation generation, cancel reads, detach subscriptions, clear badges, drafts (after confirmation) and both back stacks, then start Harbor. Check origin and generation before applying any result. Keep pending operation references in a protected per-profile store.

### 4. Reconnect and close the race

Subscribe before loading current bounded state, deduplicate hints and reread resources dirtied during load. On resume query unresolved receipts for the active profile. With controlled completion signals, deliver the held Northstar response and event during Harbor; neither may update Harbor. Repeat Northstar → Harbor → Northstar and deliver the first activation’s result last.

### 5. Check user-visible completion

Capture a switch on a phone, press Back, force-close and relaunch. Confirm the destination heading, cards, business focus, unread count, and that the next command target belongs to the active enterprise. Disable one membership while connected and repeat after a directory outage.

## Acceptance example

Northstar returns Games plus one unread message; Harbor returns Games plus none. Delay Northstar, switch to Harbor and release its reply after Harbor renders. Expect Harbor/Games with zero unread, no prior Northstar page in Back, and no stale content after relaunch.

## Verify and hand off

From the C-Sweet repository root in PowerShell, run the following during implementation. Filters that include proposed mobile suites require the issue to add those suites. Check discovery and the executed test count.

```powershell
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj --filter "FullyQualifiedName~BusinessNavigationTests|FullyQualifiedName~MobileSessionSwitchTests"
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj --filter "FullyQualifiedName~ApplicationRealtimeHubTests|FullyQualifiedName~MobileEnterpriseTests"
```

Attach ordered callback/activation assertions, real device recordings, two separate HQ audit traces, and a cold-start trace. Label simulated races as unit evidence. M3 also requires physical network/restart recovery. Record evidence in the [acceptance register](acceptance-and-operations.md) against the relevant [workflow rows](workflow-parity.md).

[Handbook and phase dependencies](README.md) · [Screen designs](ui-design.md) · [Architecture](architecture.md)
