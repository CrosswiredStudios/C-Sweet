# Forecast-driven planning

C-Sweet plans from outcomes, priority, acceptance criteria, dependencies and authorized resources.
Missing dates never prevent planning, feasibility work, estimates or a staffing proposal. Sprints
are delivery increments; readiness and acceptance evidence govern transitions. Dates do not create
capacity or grant spending, quality waivers or release authority.

## Platform policy and forecasts

`ForecastDrivenPlanning.Instructions` supplies the policy to every brokered agent model invocation
through `PlatformLlmCapabilityHandler.StreamAsync`, and to platform workflow model runs through
`AgentFrameworkAgentRunner.BuildMessages`. Existing packages receive the platform policy after a
host restart. It explicitly supersedes the assumption that an absent deadline needs human approval.

`WorkSprintReportBuilder.BuildAsync` reads the current executable scope, excluding cancelled work.
`DeliveryForecast.WithCompletionRange` derives a completion range from actual elapsed seconds per
delivered point in the latest twenty completed increments on the same board. It requires at least
two valid samples and no unestimated work or unresolved blockers/approvals. Sparse evidence returns
an explanation with no invented dates. Remaining work changes refresh the range on the next read.
The empirical fastest-to-slowest range assumes comparable work and capacity; it is neither a
statistical confidence interval nor a commitment. Queue, review and wait time are part of elapsed
history. Future resource changes and blocked dependencies can invalidate that comparison.

`WorkSprintForecastResponse` and WorkManagement.Contracts 5.1.0 `WorkSprintForecast` expose generation
time, range endpoints, history count, unestimated/blocked item counts and basis. Both the authorized
human report and `WorkManagementCapabilityHandler.ToWireReport` expose the same values.

Project `TargetDate` remains a compatible optional field for a supplied external constraint.
Project and approval views label it explicitly. Existing sprint dates are displayed as planning
horizons without an overdue countdown; no synthetic dates or fixed timeboxes are introduced.
Runtime limits, leases, scheduled operations, legal obligations, decision aging and stalled-work
detection retain their operational semantics. No historical date or approval is rewritten.

## Naomi and Gabriel

Creative Director `PlanningIntake.CapturePlanningConstraints` retains exact manager statements and
source-message IDs. Initial intake asks for external dates and reasons, resource limits and reserved
decisions while explicitly offering forecast-driven delivery when dates are absent.
`PlanningContext` is appended to the pitch before approval, making it part of the exact document
Gabriel receives. `BuildExplicitMemoryProposals` scopes project constraints to the project or intake
conversation; only explicitly standing planning preferences enter relationship memory. Current
operating state remains authoritative when governed memory is unavailable.

`ReviewProducerPitchAsync` distinguishes planning readiness from production, spending and release
lock. Missing specialist feasibility evidence can be planned work. `DirectorReview.RequiresOwner`
and `OwnerQuestion` identify a real authority gap. Repeated unchanged, unready reviews also trigger
a bounded management decision instead of more automatic discussion.

`PitchEscalation.EscalatePitchAsync` creates an idempotent `video-game.management-direction.v1`
decision under the existing human-required `material-strategy-change` rule, retaining exact session,
project, Producer, document/revision/hash and pitch bindings. The platform persists the decision and
its event outbox together and presents its normal owner decision card. Naomi returns `Blocked`,
which parks that collaboration and preserves its draft; no polling loop or synthetic approval is used.

`HandleDecisionDecidedAsync` and bounded portfolio reconciliation call
`ReconcilePitchDecisionAsync`. They re-read current authorized decisions/session, leave pending or
owner-paused work parked, and resume only Naomi's matching blocked session. An event that arrives
before the pause commits is recovered by the next normal review. On resumption Naomi relays the
recorded outcome and rationale as a `PitchReply` with `OwnerDecisionId` before Gabriel reassesses
readiness. Duplicate discovery sees the active session; transcript receipts prevent repeating a
committed relay. A new unresolved issue after an answer receives a distinct stable decision key.
Gabriel reuses an identical immutable revision and submits it only on genuine planning readiness.
Naomi still must accept that exact submitted revision before staffing can proceed.

## Verification and rollout

Regression coverage: platform `DeliveryForecastTests`, `WorkSprintServiceTests`,
`WorkManagementCapabilityHandlerTests`, `PlatformLlmCapabilityHandlerTests`, `ProjectsWorkspaceTests`;
contracts `ForecastRangeIsAdditiveAndOldPayloadsRemainReadable`; Director `PitchEscalationTests`,
`PlanningIntakeTests`, `PitchRefinementTests`; Producer `PitchRefinementTests`.

Deploy the rebuilt hosts and Creative Director 2.1.5 / Producer 4.0.1 with the added reviewed
`communication.coordination.resume.v1` request for Naomi. WorkManagement.Contracts 5.1.0 is additive.
Do not equate source tests or package creation with live installation, owner approval or recovery
of an existing run. Historical accepted pitches stay immutable; new drafts incorporate the current
policy without deleting existing revision history.

Verified locally on October 18, 2026: platform package-consumer checks passed 148 tests (7 existing
skips); Creative Director passed 182; Producer passed 378; WorkManagement.Contracts passed 49.
Both agents passed identity self-tests. All three `.nupkg` versions and agent dependency metadata
were verified, including Contracts 5.1.0 pins. Platform and agent package-consumer builds disabled
sibling SDK/contracts references; the Director also disabled sibling memory references. Live
installation, the newly requested resume grant, and recovery of the running Muse Games session
remain rollout acceptance, not outcomes established by these tests.
