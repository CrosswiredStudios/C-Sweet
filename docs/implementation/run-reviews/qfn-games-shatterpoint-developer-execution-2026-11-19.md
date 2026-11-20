# Shatterpoint developer execution, November 19, 2026

QFN Games started Sprint 1 at 2:07:34 p.m. Pacific. Adrian claimed ticket `VG0DA54B3DF0-10` three seconds later. This comparison starts at the claim and covers broker streaming, workspace preservation and the first diagnostic during implementation.

## Historical baseline

A read-only PostgreSQL snapshot identified agent work item `6c5e05e6-7019-448a-ad11-475d9f4a375a` for ticket `67cf1975-0118-4aa6-a674-2d5dc98a7a0c`. The starting commit was `883094747758f3fdcdd00334f5ed0dfa51a97433`. The frozen baseline includes all 19 criteria, compared individually with the sprint-start audit, and the stage bindings recorded at sprint start. The retained execution plan named `pnpm run typecheck`. The provider recorded model `qwen3.8-flash-next` and a maximum output of 128,000 tokens. [Baseline JSON](qfn-games-shatterpoint-2026-11-19.baseline.json)

| Measure | Original observation | Verification after the change |
| --- | ---: | --- |
| Redundant role-only audit writes | 24,679 of 26,140 chunk records through 2:25 p.m. across 49 provider runs | 0 writes for 24,679 repeated roles in the broker fixture, including after recreating the handler |
| Git pushes | 69 in the original SDK replay | 27 against a real bare Git remote, one per changed tip |
| Checkpoint saves | 69 live requests through 2:30 p.m., with 43 distinct full payloads | 43 in the SDK replay, including new evidence on unchanged tips |
| Exact duplicate checkpoint saves | 26 | 0 |
| Cumulative provider input | 2,507,672 tokens across 70 completed calls through 2:30 p.m. Peak single input was 50,101. | The scripted comparison measures calls and elapsed time. It provides no after-value for inference tokens. |

The stream count uses provider-attempt run IDs for this assignment and ordered audit sequences within each run. Each counted repeat carried the already-known assistant role, empty text and contents, and no usage or finish reason. Those rows accounted for 28,428,864 bytes summed with PostgreSQL `pg_column_size`. Their median delay from event occurrence to ledger creation was 55.1 seconds. These historical measures exclude index sizes and do not quantify physical disk I/O. Ledger lag after deployment remains unmeasured. Removing only these repeats from the historical count would leave 1,461 chunk records.

The SDK replay preserves the ordered sequence of source and full-payload equivalence classes from the 69 audit requests. It substitutes generated file contents for original code. A Git pre-push hook counts actual pushes, including unchanged pushes. The platform fixture counts checkpoint submissions. The old SDK made 69 of each on this input. The new SDK saved all 16 evidence changes beyond the 27 source tips. The injected interruption and replacement-runtime checks run after the measured window.

## Time to a diagnostic

Three serial trials per developer, stack and revision used Release builds and a new workspace for each assignment. The order of revisions alternated between trials. Time starts when the model issues the incompatible module edit and ends when it receives the checker output naming the missing export. The numbers below are milliseconds, in trial order.

| Developer | Project check | Original trials | Updated trials | Median before → after |
| --- | --- | --- | --- | --- |
| Adrian | `pnpm run typecheck` | 659, 714, 668 | 697, 692, 628 | 668 → 692 ms |
| Adrian | `python -m unittest` | 350, 391, 377 | 373, 375, 357 | 377 → 373 ms |
| Daniel | `pnpm run typecheck` | 645, 706, 634 | 661, 667, 655 | 645 → 661 ms |
| Daniel | `python -m unittest` | 360, 353, 392 | 351, 321, 427 | 360 → 351 ms |

The scripted model first records a plan and then writes the incompatible module. If it receives no diagnostic, it inspects the consumer and explicitly runs the check on call 10. Both original developers first saw the error on call **11**. Both updated developers saw it on call **3**, immediately after the edit, in every trial. Each repaired the implementation, passed the check on the repaired source, and submitted one candidate to the fixture. The pnpm script executes the Node module loader against a missing named export. The second fixture uses Python unittest discovery against a missing import. These are real project commands and diagnostic messages.

The eight-call improvement is reproducible. Wall-clock differences vary by stack and revision. Scripted model turns carry no inference latency. The audit window does not establish when Adrian first saw this specific error in the live project, and these measurements do not establish a production wall-clock speedup or independent acceptance of the full ticket.

## Matched trials with the Shatterpoint provider

A second benchmark used the same AgentTestHost callbacks, real checks and original/updated Release sources. After the identical first edit, whenever a diagnostic was still missing, the configured Qwen provider chose between inspecting the consumer, inspecting the producer and running the project check. The harness executed only those fixture actions. Once the diagnostic arrived, the harness applied the same repair and completed the normal validation and publication path. The provider received synthetic project observations at temperature 0 with a 2,048-token output cap, thinking disabled and a three-choice JSON response. The updated developer returned the diagnostic with the edit, which eliminated this separate inference decision.

| Developer | Project check | Original trials, ms | Updated trials, ms | Median before → after |
| --- | --- | --- | --- | --- |
| Adrian | pnpm | 1,336, 1,299, 1,350 | 669, 651, 638 | 1,336 → 651 ms |
| Adrian | Python | 6,800, 910, 8,514 | 395, 427, 398 | 6,800 → 398 ms |
| Daniel | pnpm | 3,269, 5,532, 1,353 | 741, 674, 653 | 3,269 → 674 ms |
| Daniel | Python | 822, 810, 7,288 | 431, 470, 327 | 822 → 431 ms |

The original developers received the diagnostic on turn 6 for pnpm and turn 5 for Python in all three trials. The updated developers received it on turn 3. All 24 assignments reached one fixture publication after repair. The pre-edit plan, first edit and repair were controlled. The variation in the original timings includes provider response time. These are measurements of the interval under test, with no elapsed-time assertion in CI.

## Implementation and coverage

- **C-Sweet AgentHost.** `PlatformLlmCapabilityHandler.StreamAsync` tracks the known role within a message and response. Meaningful transitions, text, reasoning, tools, usage and completion continue through the existing persist-before-forward path. `PlatformLlmCapabilityHandlerTests.Shatterpoint_role_repeats_leave_only_ordered_meaningful_receipts` reopens the context and reconstructs the public events from durable records. `Role_only_wire_deltas_between_split_tool_arguments_preserve_the_complete_call` exercises the OpenAI-compatible adapter with split JSON arguments. The existing transient and persistent save-failure regressions cover retry and fail-closed delivery. The broker and provider-adapter suites passed 57 tests. The separate opt-in loopback streaming test was skipped.
- **SDK.** `PlatformGitWorkspaceClient.PushCheckpointAsync` commits pending edits and checks the actual Git tip. It reuses only the last explicitly acknowledged receipt for the materialized workspace, revision and client. Changed summary, validation, command class or input fingerprint causes another platform save. Legacy snapshot hosts can still complete uploads. Direct branch operations invalidate reuse. `PlatformGitWorkspaceRemoteTests.Shatterpoint_checkpoints_push_each_changed_tip_and_record_each_changed_receipt` covers the recorded sequence, an interrupted save, concurrent repeat calls, direct pushes and runtime replacement.
- **Adrian and Daniel.** `DevelopmentWorkflow` returns focused check output in the file-edit tool result. `PlannedVerification` selects an existing, plan-named check using the project manifest and lockfile when present. It gives type checking precedence, bounds shell output, preserves failure evidence and uses the existing confined shell. `ShatterpointExecutionTests.Assigned_developer_diagnoses_repairs_and_publishes_current_source` delivers the real assigned-work capability through `AgentTestHost` for both stacks in both agents. `PlannedVerificationTests` also covers conflicting managers, scoped commands and a missing executable. The parity check covers the new regression.

The full SDK suite passed 977 tests. Adrian and Daniel each passed 387. The shared-core parity check matched 74 files. During a concurrent full-suite run, Daniel’s existing Chromium recipe test hit a Windows file lock on DevToolsActivePort. Its isolated full-suite rerun passed. Both verified Linux bundles contain the same SDK binary as the verified NuGet package.

The original source revisions used for comparison were C-Sweet `c92a581f`, SDK `a22a5a8`, Adrian `e0d97ed` and Daniel `20e382b`. The verified package versions are SDK and Agent.Testing **5.44.2**, Adrian **4.5.5**, and Daniel **2.4.5**. Release the SDK and Agent.Testing before the agents that reference them. The broker change loads with a C-Sweet host update. Agent packages use the normal published release and Installed Agents flow.

## Repeating the checks

Run the following from the C-Sweet repository with published or locally packed versions available to NuGet. The AgentTestHost fixtures use Node, pnpm 10 and Python 3. The SDK replay also requires Git.

```powershell
dotnet test tests/CSweet.UnitTests -c Release --filter "FullyQualifiedName~PlatformLlmCapabilityHandlerTests|FullyQualifiedName~ReasoningContentChatClientTests"
dotnet test ../CSweet.Agent.Sdk/tests/CSweet.Agent.SDK.Tests -c Release --filter FullyQualifiedName~Shatterpoint_checkpoints
dotnet test ../CSweet.Agent.Engineer.VideoGame/tests/CSweet.Agent.Engineer.VideoGame.Tests -c Release --filter FullyQualifiedName~ShatterpointExecutionTests
dotnet test ../CSweet.Agent.SoftwareDeveloper/tests/CSweet.Agents.SoftwareDeveloper.Tests -c Release --filter FullyQualifiedName~ShatterpointExecutionTests
./scripts/Test-DeveloperAgentCoreParity.ps1
```
