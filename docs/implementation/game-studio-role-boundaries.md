# Game studio role boundaries and verification

## Accountable roles

| Role | Owns | Escalates or supplies evidence to |
| --- | --- | --- |
| Chief of Staff | Company leadership coverage and governed hiring fulfillment | CEO; recommends the Video Game Director for game leadership |
| Video Game Director (Marcus) | Concept discovery, platforms, business constraints, official pitch, portfolio priorities, leadership hiring recommendations, CEO cadence and format | CEO for exact pitch approval and reserved business decisions |
| Creative Director (Naomi) | Creative pillars, player experience, living GDD and cross-discipline creative consistency | Marcus; collaborates with Gabriel as a peer |
| Producer (Gabriel) | Delivery across approved games, capacity, dependencies, forecasts, staffing evidence, boards and sprints | Marcus; routes technical work to Victor |
| Technical Director (Victor) | Architecture, engineering plan, integration and code review, technical feasibility | Producer for delivery; technical evidence remains attributed |
| Game Designer | Mechanics, systems, rules and balance specifications | Creative direction and Producer delivery planning |
| Art Director | Visual language, art bible and visual review | Creative direction |
| Artist | Asset creation, provenance and engine-ready outputs | Art direction and assigned delivery owner |
| Technical Artist | Import pipelines, shaders, rigs and visual performance | Technical and art direction |
| Level Designer | Spatial layout, encounters, pacing and level evidence | Creative direction |
| Narrative Designer | Story, characters, dialogue and narrative implementation specifications | Creative direction |
| Audio Designer | Audio direction, sound/music/VO deliverables and implementation metadata | Creative direction and technical integration |
| UI/UX and Accessibility Designer | Player flows, interfaces, input alternatives and measurable usability criteria | Creative direction |
| Playtest Researcher | Consent-governed research, observations and player findings | Creative direction; does not invent test observations |
| QA | Independent exact-build verification, reproducible defects and regression evidence | Producer and delivery gates |
| Build/Release Engineer | Reproducible packaging, provenance and release-readiness evidence | Producer; public launch remains a reserved approval |

Shared goals do not transfer decision authority. A production plan is not a playable build; creative acceptance is not code review, QA, spending approval or permission to launch.

## Fresh-business flow

1. Chief leadership coverage recommends Marcus. `VideoGameDirectorAgent` retains the CEO's concept discovery and reporting preferences.
2. `PitchContinuationTests`, `GameDirectorOnboardingTests` and `GameDirectorLifecycleTests` cover durable pitch creation, exact document approval, revision requests and leadership-hiring gating. Chat agreement alone cannot unlock hiring.
3. Marcus recommends Naomi and Gabriel under himself. Naomi's onboarding contacts only her authoritative manager. She has no resource-change, project-approval or artifact-decision authority.
4. Gabriel's `ProjectFoundation.ProposeProjectFoundationAsync` requires production profile 8 and an accepted exact pitch reference, then uses `Artifacts.ReadAcceptedAsync`. The retired manager-brief profile is no longer published. Marcus reviews the Producer-authored project proposal.
5. Marcus commissions Naomi's project/team-scoped GDD via SDK coordination. Naomi submits it for independent Director review. Gabriel receives the approved pitch and reviewed GDD and develops a production brief.
6. `CreativeReviewHandoff.EnsureCreativeReviewAsync` starts an independent Naomi review of the exact brief. A durable result resumes the blocked Producer conversation. Naomi returns creative findings; Marcus decides business alignment. Revisions require a fresh review. The Producer must also declare readiness; no role can manufacture another role's confidence.
7. Gabriel proposes the missing Technical Director, Engineer and independent QA together, then plans the approved increment. Other specialists require evidence from the scoped backlog.
8. Canonical delivery executes implementation, exact source review, integration, independent QA, rework and aggregate acceptance. Marcus receives attributed outcomes and owns CEO reporting.

## SDK boundaries

`AgentInteractionInstructions.Compose` supplies lead, supporting-specialist and independent-reviewer policies. `Delegation.DelegateWithTrackingAsync` handles post-pitch creative requests. SDK delegation events and attention recovery deliver durable progress. `Communication.StartCoordinationAsync`, exact artifact reads and platform idempotency own transport, access and retry behavior. The agents retain only domain judgments, exact evidence bindings and project state; no private RPC or message-forwarding subsystem is introduced.

Naomi's `CreativeReviews` refuses another manager/project/team, mismatched revisions and self-review of her authored GDD. `ManagementStatusReport` exposes required changes and Director decisions. `TeamCompute.ReportTestUrlsAsync` sends technical readiness evidence to the delivery manager instead of broadcasting to the CEO.

## Verification

Run `scripts/Test-GameStudioWorkflow.ps1` with sibling repository paths. It disables local SDK/contract references, runs the Chief and four changed agent suites, the cross-role coordination tests, and the host delivery suite. Set `CSWEET_HIRING_TEST_POSTGRES` to a disposable loopback PostgreSQL server to include transactional hiring tests.

`CreativeHandoffJourneyTests` executes the actual Marcus and Naomi callbacks using deterministic model responses and an in-memory SDK broker. It covers pending review, acceptance, changes requested, parent resumption and replay.

`AgentDeliveryJourneyTests` uses real Git, actual Technical Director/QA callbacks and host delivery orchestration. Its Star Collector fixture starts with broken movement, fails an executable QA check, receives reviewed rework, and passes movement, collection, win, restart and boundary checks. The fixture is a real local browser game at `tests/CSweet.DeliveryWorkflow.Tests/Fixtures/StarCollector/index.html`. It is authored test content, not evidence that an unobserved live company autonomously built a game.

Local verification: 20 Creative Director, 254 Director, 450 Producer, 213 Technical Director tests; 165 Chief tests (two optional live-model tests skipped); two cross-role journeys; all 17 host delivery journeys passed, including six transactional PostgreSQL cases. Package versions and runtime self-tests were checked against published SDK 5.5.0. Chromium smoke testing verifies rendering, keyboard movement and restart; executable game rules are checked in the QA journey.

## Release and installation

Coordinated releases: Director 0.2.0, Creative Director 4.0.0, Producer 6.0.0 and Technical Director 6.0.0. Install/update from their published `.csab` releases through Installed Agents before creating a fresh business. The host portrait fix also requires an updated app/API build. No stored business, approved package record or installed runtime payload is migrated or patched.
