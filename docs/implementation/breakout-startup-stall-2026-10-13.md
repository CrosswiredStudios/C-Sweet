# October 13 Prism Break startup stall

## Finding and investigation boundary

The new **Super Awesome Games / Prism Break — Game Pitch** run stalled before production
execution. Hiring completed, but Engineer and QA could not start on the installed Office.
An independent V1/V2 capability mismatch also prevented the existing Creative Director from
being assigned. Repeated startup attempts and misleading recovery copy concealed actionable
prerequisites.

This is an investigation, not a deployed repair. Evidence was read from the running local
PostgreSQL database, local setup/certification artifacts, and source. No live records, grants,
services, agents, or work were changed. Existing unrelated workspace changes were preserved.
Times below are October 13, 2026 in America/Los_Angeles (PDT); database times are UTC on October 14.

Project: `56bb127c-9e77-4320-b1cc-780edfad1a1f`.
Board: `bb36e66f-c596-4eec-8dd2-ab221e8bef72`.
Team: `cc6a9696-a198-475c-9b91-922a42bfc4a2`.

## Observed progression

| Time PDT | Evidence |
| --- | --- |
| 19:50–19:57 | Assisted Office setup session `0d37ef5d-b8ef-43a4-b4b5-5480218eed3d` prepared the installed runtime. Its staged bundle is Office 0.8.0 and its certification is v14. |
| 20:23 | Business created. |
| 20:35 | Project approved. Pitch/GDD handoff subsequently completed. |
| 20:57–20:58 | Technical Director, Engineer and QA hired. Repository provisioning completed. Engineer and QA immediately began failing startup. |
| 21:04 | Technical planning completed and Producer published 20 board items: eight containers and twelve executable tasks. Eight tasks entered the planned first sprint. |
| 21:04–21:05 | Engineer estimate, QA estimate and QA readiness remained pending. Delivery staffing failed on the Creative Director requirement. Four health incidents opened and were delivered to the owner. |
| 21:21:22 | Engineer had 280 `StartFailed` runtime records and QA 279. Neither had a successful runtime. Both schedules still recorded zero consecutive startup failures. |

`Production Sprint 1` was still `Planned`, with **zero sprint executions and zero delivery
plans**. This was not a game-code or gameplay-test failure: implementation had not started.
Earlier interrupted Chief/Producer chat work exists, but Producer's replacement runtime
subsequently completed planning; those interruptions do not explain the current blockers.

Installed versions: Chief 2.12.0, Creative Director 1.19.1, Producer 2.20.3, Technical Director
2.16.3, Software Developer 1.17.1 (Daniel, fulfilling game engineering), and Game QA 2.8.1.
Producer, Technical Director and Creative Director checkout HEADs matched their installed
package commit SHAs during this investigation.

## 1. Installed Office cannot run the hired development agents

Daniel and Rohan both declare `software-development-polyglot-v1` and `ReadWrite` workspace
access. Their runtime records consistently contain:

> runtime.start_cleanup: No connected execution node advertises the required certified guest image variant.

The only connected Office, `2fc53625-d816-4771-abf9-5c943811c0f7`, was Ready and heartbeating,
but advertised:

- Office **0.8.0**, provider `hyperv-gen2`;
- suite **`csweet-hardware-vm-smoke-v14`**;
- `SupportsPublicInternet = false`;
- guest digest `sha256:c9ce3bfbc94aa4c0f685747d6d168382ce49a312f049bc0e8868d69732dcd1ca`.

`FleetGuestImageRegistry.ResolveAsync` requires v15 or v15-ipv4 certification and public-internet
support for this environment profile. `AgentRuntimeManager.TryStartAsync` also requires guest
protocol 1.1. Generic Office readiness therefore did not establish readiness for these agents.
Approving an agent's internet permission alone cannot supply the missing certified image.

Local corroboration: `C:\ProgramData\CSweet\Setup\b-6ccae06c84c643d1\office-bundle.json`
records Office 0.8.0. Its `artifacts\windows-test\certification-20261013-195529\windows-hyperv.json`
records the same v14 suite and digest advertised by the live Office. The corresponding setup
session's node ID matches the live node.

The earlier successful 0.9.0/v15-ipv4 deployment documented in
[runtime public internet](features/runtime-public-internet.md) belonged to a different Office
identity. It does not establish the state of this fresh installation. The evidence establishes
the installed mismatch; it does not establish why release selection offered that older bundle.

Relevant source:

- `src/CSweet.Api/Setup/Start-CSweetDevelopmentOfficeSetup.ps1`: the bootstrap invocation
  passes `PrebuiltRoot` when supplied, but does not request `DevelopmentTools` or the certified
  IPv4 development variant. The name "development setup" alone does not select that payload.
- `src/CSweet.Infrastructure/Setup/AgentRuntimeEligibilityService.cs`, `EvaluateAsync`:
  checks general fleet readiness rather than resolving the installation's required guest profile.
- `src/CSweet.Infrastructure/Setup/FleetGuestImageRegistry.cs`, `ResolveAsync`: correctly refuses
  the incompatible image. Keep this enforcement.

## 2. On-demand startup failure bypasses retry suppression

`AgentRuntimeManager.EnsurePendingOnDemandRuntimesAsync` rediscovers pending work and queues
another runtime whenever no active runtime exists. A failed start is terminal, so it immediately
qualifies again on the next reconciliation pass. `HandleAlwaysOnTermination` implements failure
counting/backoff only for AlwaysOn agents; Engineer and QA are OnDemand.

At 21:21 there were **559 failed starts in about 23 minutes**, approximately one per five seconds
per agent. Their collaboration inbox items still had `AttemptCount = 0`: an unstarted runtime
never claimed the work, so inbox attempt limits did not bound startup retries. Repeated attempts
cannot install a missing guest image.

## 3. Existing Creative Director rejected by mixed protocol requirements

`VG56BB127C9E-9`, **Level 1 Grid Spec + Beat-Map Template**, requires role `creative-director`
and `work.execution.run.v1` on `specialist-execution`. Naomi is an active member of this team.
Her installed manifest and approved provided-capability grant include **`work.execution.run.v2`**
but not V1.

The failing path is deterministic:

1. Technical Director `PlanningValidation.PlanValidationIssues` admits only V1 in proposed
   execution capability requirements.
2. Producer `SpecialistAgent` appends V1 when publishing planning, choosing an owner and deriving
   coverage requirements.
3. SDK `HierarchicalProjectDelivery.PrepareAsync` / local `Select` preserves those requirements
   and adds V2. The effective requirement is **V1 AND V2**.
4. `RoleTaxonomy.IsEligible` requires every capability exactly. Naomi fails the V1 check.
5. The generic exception says to assign a Creative Director "with V2 execution", obscuring the
   missing legacy capability. The author-selection call has no excluded authors; its generic
   independence wording does not prove an independent-review conflict here.

SDK files are in the current `../CSweet.Agent.Sdk/src/CSweet.Agent.SDK/` checkout. Producer and
Technical Director files are in their corresponding sibling agent repositories.

The Producer consequently proposed another Creative Director coverage slot requiring V1.
Request `3b67122c-d501-454e-afc8-bac59be547bd` reached `RevisionRequested` at 21:04:40 because
its flow-metrics evidence revision was stale. At the captured snapshot it had not become a new
approved hire. This is a separate recovery weakness, not evidence that the owner missed a hire.

The level-spec task and the Technical Director's planning/document task were also persisted
with `DeliveryKind = Code`; `ProposalDeliveryKind` special-cases only QA as Artifact. Audit
classification during the repair, but this observation alone does not establish an additional
execution failure: hierarchical delivery also considers the author role when choosing code work.

## 4. Monitoring found the failures but gave incorrect recovery advice

Health detection and owner delivery worked. Four open incidents covered the three pending
collaborations and the staffing conflict. Runtime diagnostics retained the missing-image reason.

However, the three owner messages said "A retry is queued" and "No action is needed while the
current attempt runs." The current work was Pending, never claimed, and no viable runtime existed.
`ProjectHealthService.PresentAsync` in `ProjectHealthService.Presentation.cs` treats both Pending
and Leased as sufficient to overwrite recovery guidance with that reassurance. It does not check
the current runtime's startup blocker in this branch. The staffing incident's owner copy likewise
said only to review a dependency, omitting the capability mismatch retained in detailed evidence.

## Recovery order for this run

1. Use the supported Office maintenance path to install a compatible, destination-certified
   development payload. Drain and confirm no active assignments before replacement. Verify the
   **live** Office advertises the expected digest, protocol 1.1, v15/v15-ipv4 suite and supported
   address families after reconnect. Preserve normal installation permission review.
2. Confirm Engineer and QA establish broker sessions and consume their existing queued estimate/
   readiness work. If those items have expired (captured deadlines were about 22:04 PDT), use normal
   collaboration recovery on the existing sessions after reviewing saved effects.
3. Apply the single execution contract cutover and republish affected unstarted planning with revision
   checks. Do not grant a capability an agent does not implement or hire a duplicate to hide the
   mismatch. Revalidate whether level-spec authorship belongs with Creative Director or a design
   specialist, preserving acceptance criteria and independent review.
4. Resolve or supersede the obsolete coverage proposal through normal manager operations, refresh
   estimates/readiness and run delivery preflight. Success means a delivery plan exists, the sprint
   activates and an exact first implementation candidate reaches independent QA.

## Prevention work and acceptance gates

| Priority | Change | Required proof |
| --- | --- | --- |
| P0 | Make setup/release selection carry an explicit required environment profile and compatible certified payload. Extend installation/team readiness to report missing profile/protocol/certification before kickoff. | Fresh setup with a Ready v14 Office reports Engineer/QA unavailable with the exact prerequisite; compatible live v15 development setup starts both agents. |
| P0 | Align proposed capabilities, Producer assignments and SDK hierarchical execution on the actual protocol. Use explicit versioned compatibility rules, not unconditional V1+V2 conjunction. | An installed V2-only eligible Creative Director can be selected on an appropriate V2 task; genuinely unsupported roles/capabilities and independent-review conflicts still fail. Repeat with published packages and sibling project references disabled. |
| P1 | Persist a startup prerequisite block and bounded retry policy for OnDemand as well as AlwaysOn. Wake on relevant fleet/configuration changes, re-read authority and retain bounded reconnect discovery. Save state/outbox atomically. | Missing-image failures stay bounded across repeated ticks and service restarts; duplicate/out-of-order inventory notifications do not cause duplicate launches; a compatible inventory update resumes existing work once. |
| P1 | Give current runtime blockers precedence over Pending inbox state in incident presentation. Distinguish waiting, eligible retry, claimed execution and prerequisite failure. Include exact failed staffing requirement and remediation owner. | Pending work plus StartFailed shows an Office repair action; zero claimed attempts are not described as running. A healthy leased retry can show recovery in progress. |
| P1 | Reconcile `RevisionRequested` coverage from fresh authoritative evidence and recheck existing employee capability before proposing hiring. Audit artifact/code classification at the same boundary. | A stale metrics revision produces one revised/superseding proposal without duplicates, dropped scope or an endless unchanged wait. |

The subsequent [single execution contract fix](features/work-management/canonical-execution-contract.md)
supersedes the version-compatibility proposal above: both old execution APIs are removed in
favor of one coordinated development contract. That document records source/package verification
and the remaining live-run prerequisites; the observations here describe the original incident.

The next-run acceptance scenario must use the **actual installer, released agent packages, approved
grants and connected Office inventory**, then proceed through hiring, planning, estimates, readiness,
first implementation and QA. Source/unit success and a manually upgraded prior Office did not cover
this fresh-install path. No new tests were run for this investigation; the proposed regression and
live acceptance checks above remain work to implement and execute.
