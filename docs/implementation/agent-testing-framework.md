# Standalone agent testing framework

The SDK provides `AgentTestRuntime` and the standalone `AgentTestHost` dispatcher. This plan tracks the testing package, authoring templates and CI adoption.

An author working in a single agent checkout should be able to start at the callback that failed, make an edit, and rerun the scenario with `dotnet test`. A new agent author should be able to develop a flow the same way before installing the agent in C-Sweet. Both workflows use the real agent and assert its observable result.

## Run Victor's stack decision against a specific model

This checkpoint starts with an accepted pitch, a staffed team, an empty board and a pending Producer planning wake. It runs in memory with the real Technical Director and Producer. It needs the workflow suite and its source-repository dependencies. No C-Sweet instance or database service is needed.

From a C-Sweet checkout, run the following PowerShell command with your OpenAI-compatible API base URL and the model ID served there.

```powershell
./scripts/Test-VictorStackCheckpoint.ps1 -BaseUrl 'http://localhost:8000/v1' -Model 'your-model-id'
```

If you keep the Director repository elsewhere, add `-GameDirectorRepositoryRoot <path>` or set `CSWEET_GAME_DIRECTOR_REPOSITORY`. Use `-SourceRepositoriesRoot` for a different parent directory for the other agents. For an authenticated endpoint, pass `-ApiKeyVariable MY_MODEL_KEY` with the key already set in that environment variable. The fixture uses a 4,096-token output budget by default. Use `-MaxOutputTokens` to match the installation under investigation.

The [starting fixture](../../tests/CSweet.GameRoleWorkflow.Tests/Checkpoints/victor-stack.json) contains the accepted pitch and its observable outcome. Copy it, edit those two fields and pass `-Checkpoint <file>`. Use `-NoBuild` when changing the fixture, model or endpoint. Omit it after editing agent or harness code. Each invocation creates fresh platform state so a previously accepted stack decision cannot satisfy the next trial.

Only Victor's `project_technical_choice` request goes to the selected endpoint. The test uses the app's `PlatformLlmCapabilityHandler` and provider factory, so the structured schema, fixture employee identity, workspace instructions and installation output budget go through the production code. Other model judgments are scripted. The output shows the generated choice, inference time, and the assigned setup ticket that Gabriel published. The assertion checks the accepted stack and setup against that ticket. It allows different engines to pass. One invocation reports one trial. The ordinary `Category=AgentHarness` run exercises the handoff offline.

Each trial allows one generation. If Victor requests a correction, the test fails and includes the first reply in its output. The test result and reply are also saved in `artifacts/TestResults/victor-stack-*.trx` in the checkout that ran it.

This pilot covers the accepted-pitch checkpoint in the game workflow suite. Capturing an arbitrary running company and selecting other live steps are separate roadmap items.

## Regression coverage for flow fixes

Every agent-flow fix ships with an accompanying `AgentTestHost` regression. The rule applies to the contributor or AI copilot working on the fix, including when the code change belongs to Headquarters. Reproduce the failing delivery through the SDK dispatcher, script only inputs outside the behavior under test, then drive the receiving agent or platform service until its observable effect is recorded. Keep an ordinary scheduled wake distinguishable from an operator retry. For a restart, keep the platform state, replace the agent instance and redeliver the original event ID.

`FreshTeamSprintJourneyTests` in `tests/CSweet.GameRoleWorkflow.Tests` exercises this boundary for game planning. It starts with a staffed team and an accepted pitch on an empty board. The two cases use scripted technical judgments for different game mechanics and follow the selected stack into the published setup ticket, the active sprint and the assigned engineer's inbox lease. Run both with `dotnet test tests/CSweet.GameRoleWorkflow.Tests -c Release --filter FullyQualifiedName~FreshTeamSprintJourneyTests`. Use the same sibling checkout paths as `scripts/Test-GameStudioWorkflow.ps1`.

## Implementation status

The SDK pull request introduces the shared dispatcher, standalone host and revision-aware operating-state fixture in version `5.42.0`. The Producer pilot covers Brickstorm recovery across restart and duplicate delivery. The Director pilot exercises pitch review from a standalone clone with the packed dependencies. [SDK pull request](https://github.com/CrosswiredStudios/CSweet.Agent.Sdk/pull/12) · [Producer pull request](https://github.com/CrosswiredStudios/CSweet.Agent.Producer.VideoGame/pull/7) · [Director pull request](https://github.com/CrosswiredStudios/CSweet.Agent.Director.VideoGame/pull/12)

The Director pilot measured 3.18 seconds at p95 across 20 warm edits, within the 15-second target. Shared operating state reduced scenario setup from 69 to 52 lines, leaving 22 lines above budget. The original author was familiar with the harness. Independent onboarding, automatic discovery by Codex and Claude Code, and the required CI check are still open. [Pilot measurements](https://github.com/CrosswiredStudios/CSweet.Agent.Director.VideoGame/blob/a566461bde2c0e34eb058d11e3814b999c42856e/docs/pitch-review-harness-pilot.md)

## First release

| Owner | Deliverable | Acceptance evidence |
|---|---|---|
| SDK | Share the delivery switch and callback routing from `AgentRuntimeWorker<TAgent>` with the test host. Cover configuration, calendar, attention, coordination answer validation and submission, and personal-work draining. Preserve existing `AgentTestRuntime` callers. | Worker and host route the same fixture through the same SDK code. An invalid coordination answer produces no submission. Duplicate delivery of an accepted answer leaves one stored turn. The production worker owns leases, renewals and reconnects. |
| SDK testing package | Ship `CSweet.Agent.Testing` as a separate public NuGet package from the SDK repository. Support a seeded callback, a fresh agent instance over retained fixture state, scripted streaming model responses, and a journal of attempted calls, accepted effects, progress and rejections. | A test identifies the first relevant mismatch. An unconfigured fixture capability fails the scenario even if the agent catches the exception. An explicitly configured platform denial exercises the agent's recovery path. |
| Headquarters and SDK | Share effective model request construction and provider adaptation. Compare outgoing messages, instructions, tools, schema, parameters and returned chunks at a recording HTTP transport. Headquarters resolves authorization and production context. | A named generation can use an explicitly selected OpenAI-compatible profile while the rest stay scripted. Give the Producer adaptive-review call site a stable invocation kind. A missing match fails the run. |
| Producer and Director | The Producer maintainer reconstructs the November 17 Brickstorm checkpoint, marks reconstructed fields, and scripts the blocked Technical Director response. The Director maintainer authors a pitch-review scenario using the same package. Start shared fixtures with operating-state reads and writes, artifact and roster reads, and one coordination session. | The affected Producer revision reproduces the stall. The fix schedules a fresh generation attempt, and redelivery of that attempt creates one session. The Director scenario runs from a standalone checkout with packed dependencies. |
| Evaluation tooling | Configure the model under test and judge independently by endpoint, credential reference, model and parameters. Support one live run by default and explicit repeated trials. Use deterministic state assertions first and an optional plain-language criterion evaluator with human-labeled examples. | Each live trial starts from fresh state. Report passes, behavioral failures, provider errors and judge errors. Verify quoted evidence against the emitted text and allow a missing-evidence result for an omission. Cache validated verdicts by text, facts, criterion, evaluator version and judge settings. |

## Planning handoffs and regression scenarios

Use Brickstorm for the first Producer regression and keep the Director pitch review as the independent package-adoption test. The November 17 Prism Break run adds a different failure. After a manual retry, Victor returned a validated Story at 21:42:44 UTC. Gabriel acknowledged it, read an older wait assessment, and deferred again at 21:42:52. The export records an earlier CEO escalation and the later manual retry. The timing of any earlier automatic retry needs a durable scheduling receipt. [Prism Break event snapshot](run-reviews/qwen-games-prism-break-ea3b1a6d-2026-11-17.events.json)

Ship the two first-release scenarios above. Next, prioritize the failed and recovered Story cases below. Schedule the reporting, staffing and page-correction cases as those behaviors change. Each test delivers the real callback for one actor against a scripted counterpart. Seed its own checkpoint and the session, card, roster, board or artifact revisions that actor can read. Between deliveries, change a record or deliver an older wake. Record which actor made each call, which revision supported its decision, who owns the next action, and what was accepted.

| Scenario | Change between deliveries | Observable result |
|---|---|---|
| Brickstorm roadmap recovery | The Technical Director session ends with a generation failure. The Producer has retry budget. Restart the Producer after its card is deferred and deliver the eligible wake twice. | The card records the scheduled retry. One new generation attempt is accepted with the diagnostic and a fresh request key. Duplicate delivery of that attempt reuses the key. |
| Failed Story refinement | Fail the request for a specific Story with retry budget remaining. Keep its approved scope and eligibility intact, seed an older wait assessment, restart the Producer and deliver the due wake twice. | The Technical Director receives one new request for that Story with the prior diagnostic and a fresh idempotency key. The duplicate wake creates no second session. Already admitted Stories keep their board identities. |
| Prism Break Story recovery | Seed a valid earlier wait, then complete the same scoped session with a newer, validated Story artifact. Deliver the completion and an older wake. In a control case, keep a binding hold on that Story in force. | The Producer re-reads the session and reassesses the exact revision. With prerequisites satisfied it admits the Story once. With the current hold it retains the draft and records the specific dependency. A reply from a different session cannot release the hold. |
| Recovery reporting | Present the same specialist failure to the reporting actor in two fixtures. One has a confirmed retry and due time. The other has exhausted its budget and a current blocker with a named owner. | The first produces recovery status with the recorded next action. The second routes an actionable blocker to its owner once. A counter or a specialist's suggestion to escalate is insufficient evidence that a retry was scheduled or that a CEO decision is due. |
| Staffing during ownership review | Complete a hire after a review began. In a second case add a genuine conflict between two approved requirements affecting one Epic. Redeliver the earlier review wake. | Current roster facts resolve the staffing question. The ordinary gap may be tracked while eligible work proceeds. The binding conflict holds its affected scope, and independent work can still advance. |
| Technical Director page correction | Script a page with several uncited entries, then a reply that fixes one problem and introduces another. Exercise exhaustion and a corrected response. | The correction identifies the rule and affected entries that can be checked together. Later prompts retain applicable earlier corrections. Accepted pages survive retry. Exhaustion returns the failed page and useful diagnostic to the requester. An invalid page cannot produce a completed roadmap. |

The Producer already records a failure reason and increments `StoryRetries` in `RetryStory`. The next adaptive pass can still ask the model to select work. Drive the due wake through the public callback and assert the accepted coordination request. That distinguishes recovery of the failed Story from both roadmap regeneration and admission of a Story already returned.

The page-correction scenario builds on `ValidateCommitments`, `ReadPlanningPageAsync`, `AdaptiveRoadmapTests` and `PlanningPagesTests`. Technical Director 6.13.3 already has specific task-list diagnostics. Use focused validator tests for individual rules and one public callback scenario for the correction and handoff. Malformed JSON can prevent field-level checks. Test the errors the validator can actually identify at that point.

### Project state and later coverage

Use phase names to organize game-planning fixtures. For a retry, read the current card and coordination session. For publication, verify the accepted board items against the source revision. A project can be refining one Story while another awaits staffing. A single project-wide stage would discard that distinction. Exercise existing authorized reads first. If an actor cannot discover a fact it needs, record the actor, missing fact, authoritative source and revision. Evaluate a scoped read-only projection against that gap in a separate platform design. Execution continues to require the current grant and claim. `ProjectHealthReader` and `ProjectHealthService.IncidentRequiresOwnerInputAsync` already distinguish some active recovery from requests for owner input. [Project health and owner action](features/producer-project-health.md)

After the pilots, seed an already-published Story and exercise estimation and sprint readiness directly. The Producer's `HandlePersonalTodoAsync` already routes these commitments separately from planning. Keep Shardfall's active-sprint document-review loop as a separate execution scenario. [Planning lifecycle paths](planning-lifecycle-code-trace.md) · [Shardfall execution review](run-reviews/qfn-games-shardfall-sprint-1-flow-2026-11-16.md)

For the upstream brief question, trace one disputed Epic to the approved brief revision and distinguish a requested outcome from a constraint. Brickstorm had 428 numbered sources and 42 commitments. Some rejected entries described manager direction that was absent from the numbered sources. A fixture should preserve both inputs and check which one may support a citation. To evaluate model judgment, hold the fixture and criteria fixed and explicitly select the generation for repeated live trials. The counts alone establish neither scope inflation nor model quality. [Brickstorm incident review](run-reviews/qfn-games-brickstorm-2026-11-17.md)

## Development pipeline for Codex and Claude Code

**User story.** An author asks either coding assistant to implement a flow or fix an agent. Starting in that agent's repository, the assistant discovers how to exercise the behavior, runs the relevant harness scenario, and supplies enough evidence for a reviewer to reproduce the result. The author need not mention the harness in the task prompt.

### Repository instructions and starter project

The SDK maintainer owns the standard verification instructions in `templates/CSweet.Agent.Template/AGENTS.md`, a runnable example test and the authoring guide. Generate a root `CLAUDE.md` that imports the adjacent instructions with `@AGENTS.md`. When adopting an existing repo, merge the verification section into its own `AGENTS.md` and preserve existing Claude-specific guidance around the import. Keep the commands and example available in a standalone checkout. In the C-Sweet contributor guide, point cross-repository work to the affected agent's verification section. [Codex repository instructions](https://developers.openai.com/codex/guides/agents-md/) · [Claude Code project imports](https://code.claude.com/docs/en/memory#share-one-file-with-other-coding-tools)

The verification section should tell contributors when to use the harness, give the repository's exact test command, and link to one small working scenario. Include changes to prompts, callbacks, capability use, handoffs, retries and persisted state. Keep ordinary unit tests for pure logic. Document the fixture location and the explicit live-run command alongside the example.

### Local development and handoff

1. Pick the observable outcome before editing. For an incident, seed the last durable state and retain the failing reply when available. For a new flow, seed the smallest valid starting state and write the expected effect. A provisional capability fixture must identify the contract it assumes.
2. Run the focused scenario against the starting revision and record what failed. Implement the change and rerun the same scenario through the public delivery path. When retries are involved, deliver the wake again and assert the durable effect count.
3. Mark deterministic harness tests with the xUnit trait `[Trait("Category", "AgentHarness")]`. The proposed common command from a generated agent root is `dotnet test --filter "Category=AgentHarness"`. A fully qualified test-name filter selects one scenario during editing. Run the repository's full `dotnet test` suite before handoff. Both commands must work after package restore with Headquarters stopped and model endpoints unavailable.
4. In the PR or handoff, give the scenario name, exact command, result, and durable effect observed. For a fix, include the before-and-after outcome. If execution was blocked, give the command and reason. Record any explicitly requested model or judge run separately from deterministic results.

### Pull requests and releases

Each adopted agent repo gets a PR check named **Agent harness**. Extend the existing read-only PR workflow where available. The pilot entry points are the Producer's `.github/workflows/extension-ci.yml` and the Director's `.github/workflows/ci.yml`. Use the same deterministic test selection as local development and restore pinned released packages with sibling project references disabled. Use the existing exact-version dependency preparation where needed. Run the check on incoming PRs, including changes to prompts, fixtures and instructions. [Agent CI dependency fallback](agent-ci-dependency-fallback.md)

The job writes fresh machine-readable test results for the current revision. It fails on test failure, missing results, or zero executed harness tests, including a filter that selected nothing or a suite whose cases all skipped. Emit the executed count, scenario names and failed assertions. A reviewer checks that the scenario exercises the changed behavior. A nonzero count alone only establishes that tests ran. Endpoint configuration must never enable a live model or judge implicitly in this job.

Make the check required for merge when each repo adopts the harness. Its release workflow must obtain a passing result for the exact revision being packaged, including tag or manual releases. Retain the repo's existing unit tests, self-test, version checks and installed-runtime acceptance. Installed acceptance covers effective grants, broker behavior and the installed package. [SDK testing and release guide](https://github.com/CrosswiredStudios/CSweet.Agent.Sdk/blob/main/docs/testing-and-release.md)

### Rollout and verification

| Stage | Owner and exit condition |
|---|---|
| Package and template | SDK maintainer packs both packages and extends `scripts/verify-authoring-template.ps1` to generate a clean repo, check the instruction import, and execute the example against the packed versions. A deliberately broken assertion fails CI. A run that discovers zero scenarios also fails. |
| Pilot repositories | Producer and Director maintainers adopt the published package and enable the PR and release checks. In separate fresh Codex and Claude Code sessions, give each assistant an ordinary behavioral change without naming the harness. Verify it finds the example, runs the relevant test, and reports the observed outcome. Use local checkouts independent of personal instruction files. |
| Adoption targets | On a named reference machine, target p95 at or below 15 seconds from edit through incremental build and scripted verdict across 20 warm runs. Target 30 minutes and at most 30 nonblank lines of scenario setup for the second maintainer, including custom helpers. Record seed data size separately. Use the pilot measurements above as the baseline for the next maintainer. |
| Existing agent fleet | Inventory maintained agents by repository and SDK version. Track the pinned testing package, first meaningful scenario, instruction files, required PR check, release verification and maintainer. Roll out by repo after the pilots. For Adrian and Daniel, carry shared execution regressions through both suites and run the existing developer-core parity check. |

## Follow-on work

After the pilots, add recorded replay through the first divergence, model comparison across scenarios, and out-of-process support where an agent needs it. Use one existing developer-agent workspace recovery test to establish which filesystem and checkpoint fixtures that work requires.

Inventory retained model-request evidence before implementing capture. Capture is planned for all instance types. In hosted instances the customer enables capture and controls any transfer to C-Sweet. Carry existing content-omission rules into transcripts and exports. Track capture separately from the deterministic pipeline so both new-flow development and incident regression can ship with hand-built fixtures.
