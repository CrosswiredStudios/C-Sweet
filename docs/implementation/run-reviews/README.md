# Business startup run comparisons

Track what agents actually exchanged, decided, and completed from business creation through delivery. Use this record to compare runs without mistaking inference activity, acknowledgments, or a successful retry command for completed planning.

## Runs

| Run | Installed Producer / Director | Last verified milestone | Interpretation |
|---|---|---|---|
| [Qwen Games and PRISM BREAK, November 17 afternoon, organization ea3b1a6d](qwen-games-prism-break-ea3b1a6d-2026-11-17.md) | 6.22.1 / 0.14.1, Technical Director 6.13.2 → 6.13.3 | Eleven Epics. Human retry returned one Story and two validated briefs. Sprint 1 remained Planned and empty. | Producer resumed an older wait after the specialist succeeded. Both proposed tasks required an engineer absent from the roster. |
| [QFN Games and Prism Break, November 16 evening](qfn-games-prism-break-2026-11-16.md) | 6.21.1 / Director version unverified; Technical Director 6.13.0 | Eleven epics and six validated task briefs retained off-board. Sprint 1 Planned and empty at 9:05 p.m. | The Producer reselected the same Story after a resource change. Its review input omitted the completed draft. The next admission review was scheduled for 9:16 p.m. |
| [Qwen Games and Prism Break, November 16](qwen-games-prism-break-2026-11-16.md) | 6.21.0 / 0.14.1 | Eight Epics; retained technical Story draft; Sprint 1 Planned | Self-directed consultation and changed-facts recovery fixed in published SDK 5.39.0 / Producer 6.21.1. Live rollout and active Sprint 1 verification pending. |
| [Qwen Games and Prism Break, November 15](qwen-games-prism-break-2026-11-15.md) | 6.16.0 / 0.11.0 | Production brief accepted; staffing returned twice; no project tickets or sprint | Earlier-version baseline. The owner plans to replace this business. This is not an acceptance test of 6.17.0 / 0.12.0. |
| [QFN Games and Brickfall, November 15](qfn-games-brickfall-2026-11-15.md) | 6.17.0 / 0.12.0 | Production brief accepted; two staffing returns; timer revised first proposal but both decision wakes retained stale waits | Updated agents verified. Purpose truncation and fixed assignment fields prevent sufficient revision. No technical hire or Sprint 1 at 7:07 p.m. observation. |
| [QFN Games and Shardfall, November 16](qfn-games-shardfall-2026-11-16.md) | 6.18.0 / 0.13.0 → 6.19.0 / 0.14.1; Technical Director 6.11.0 | Original estimate accepted; Story advice reference saved; retained feasibility Completed and certified provider selected | Operator-assisted recovery verified at 11:08 a.m. All three investigated failures cleared. Sprint 1 remains Planned with reported zero capacity; no active sprint is claimed. |

## Updating the living record

Keep one report and one chronological event ledger per business, identified by organization ID and start time. Names can be reused. The report is the readable account; the JSON ledger holds timestamps, participants, results and source references for reproducing the timeline. Update this index when another observation or run is added. These are observation records, not an automatic monitoring service.

For each observation:

1. Record the capture window, organization/project IDs, actual installed package versions, relevant configuration and requested game scope. Record unavailable configuration as unknown. A source manifest, published release and installed package are three separate facts.
2. Read the roster, approval records, personal work, project boards/sprints, and communications from the CEO and relevant agent perspectives. Read exact failed-event details where a failure matters. Follow pagination before claiming exhaustive audit coverage.
3. Add interactions in event-time order. Each event needs a stable local ID, UTC timestamp, sender, recipient, short purpose, observed result, next accountable owner and source reference. Preserve corrections and returned proposals; do not rewrite them as if they had always been approved.
4. Append a dated observation rather than replacing prior state. Deduplicate repeated reads using source IDs. Merge projections of one coordination turn into one event; keep distinct messages and decisions distinct. Group routine acknowledgments only when the report says so.
5. Separate approved direction, agent judgments, platform rejections, source-based explanations and hypotheses. Name the operation that is blocked and identify which work remains independently possible. Do not turn a reviewer preference into a universal workflow rule.
6. Confirm real transitions: approved request, fulfilled hire, accepted artifact revision, created ticket, active sprint, accepted increment and final project completion. Successful inference and prose saying “ready” are supporting evidence only.

Use [the live run analysis playbook](../live-run-analysis-playbook.md) for access and symbol-level source tracing. Keep credentials, raw prompts containing secrets, and unrelated company data out of committed records.

## Comparison measures

Report elapsed times from both business creation and accepted pitch. Record each milestone timestamp, or **not reached**; a missing milestone is not a zero-duration success. Compare the same milestones without requiring the agents to follow a fixed number or order of planning passes.

| Measure | Evidence |
|---|---|
| Leadership and initial team available | Approved hiring workflow plus employee/installation roster |
| Project, GDD and production brief accepted | Exact approval and artifact revisions |
| Missing capability identified, approved and available | Staffing request, manager decision, fulfilled hire or qualified existing participant |
| First project board ticket and first executable task | Canonical project-scoped board items; exclude personal coordination tasks |
| Sprint 1 drafted, staffed, active and completed | Sprint scope, roster/resource facts, sprint state and completion record |
| Next sprint adapts to findings | Review decision, affected scope/dependencies, retained unaffected work and next sprint |
| Final completion | Final approved outcomes and evidence, open obligations and completion decision |
| Rework and recovery | Returned proposals, changed evidence, owner actions, successful continuations, duplicate effects |
| Human involvement | Questions, response timestamps and manual nudges; report human wait separately |
| Cost and activity | Complete inference/token and audit windows, with sampling limits stated |

Keep game workload visible: mechanics and their interactions, visual/audio ambition, platforms, content volume, uncertainty, existing reusable assets and requested exclusions. A richer game may correctly require different specialists or longer exploration. Compare similar scope/configuration before attributing a timing change to an implementation release. Third-party participants count when their capabilities and authority fit the work; named first-party agents are not mandatory checkpoints.

## Fresh business observation checklist

This is a measurement checklist, not additional agent gates. Capture installed versions first, then observe whether the agents can converge on scope, obtain appropriate technical coverage, publish a small executable increment and start Sprint 1 without a CEO nudge. If a manager returns a proposal, record whether a materially revised proposal follows, which requested changes are applied, and whether unaffected work continues. Follow the same record through sprint review, the next sprint and final completion. Do not invent missing timestamps or mark future milestones complete.

