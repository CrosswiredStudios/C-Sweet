# Technical review and independent QA recovery

## Production code

- SDK `TechnicalReviewJson.ReadAsync` requests a strict response schema, rejects malformed/duplicate/unknown fields and inconsistent evidence, and allows one corrective response for the failing page or final synthesis. `PlatformChatClient` and `PlatformLlmCapabilityHandler` carry the schema and provider finish reason across the broker. Installation output budgets remain platform policy.
- SDK `BranchDiffTechnicalReview.ExecuteAsync` accounts for every contiguous authorized diff page. Model content separates concrete blockers, observations and cross-page questions. Synthesis must resolve every blocker explicitly and cover every exact criterion with reviewed offsets. Overflow returns rework after complete coverage; no finding is silently dropped.
- SDK `DeliveryTaskTechnicalReview.ExecuteAsync` stores validated page evidence in operating state, keyed by policy, publication, source/target, patch digest and planning/assignment/scope identity. Resume reauthorizes and checks each page hash. A replayed write returning different accepted evidence stops synthesis. Final current-item and candidate reads precede the verdict. Commit identities are attached by code.
- QA `GameQaExecution.ExecuteCapabilityCoreAsync` reads the authorized typed current item before validation and rechecks planning/delivery before accepting evidence. Raw dispatch contains `deliverySpecificationJson`; it is not a typed `WorkItem.Delivery`. Hierarchical results always use `WorkDeliveryReviewResult`, with the exact integrated commit, complete criterion coverage and repository-bound command evidence.
- QA `GameQaReport` accepts only flat, correctly typed report arguments and returns corrective feedback for invalid arguments/evidence. No nested-report or encoded-boolean/array compatibility remains. Existing standalone artifact QA is a separate authorized workflow.
- Engineer `GameEngineerProfile.SystemPrompt` honors Ready InternalGit, the agreed platform build/preview path, preserved-candidate rework and canonical feasibility review. It calls for application/assets/malformed-path/consent checks alongside build and health checks; it cannot invent executed evidence.

JSON is the workflow transport for decisions and evidence. The reviewer still writes prose in summary, evidence and finding fields. The platform needs explicit approval, exact identity and criterion fields to decide whether integration or rework is authorized. Model-generated JSON is validated input, never a grant or an automatic merge instruction.

## Explicit task scope amendment

`WorkDeliveryService.AmendTaskAsync` is exposed only through authenticated
`POST /api/organizations/{organizationId}/work/delivery/tasks/amend` with the
platform-owned `AmendWorkDeliveryTaskRequest`. It adds no agent mutation capability
and does not change the WorkManagement.Contracts package.

The actor must have the normal project configuration/board read grants and be the
project/release manager or organization owner. The plan must be paused, the task
blocked, revisions current, and all affected attempts drained. Promoted scope is
immutable. Only requirements, acceptance criteria and constraints may change;
dependencies, staffing, delivery kind and all accepted artifact/architecture
identities are preserved. `UsePlatformPreview` requires Ready InternalGit and an
assigned release build-readiness stage, and selects its existing `compute-test`
readiness path.

One serializable transaction updates task planning/delivery and revision pins,
invalidates prior aggregate candidates, revokes their stage grants, records the
mutation receipt and notification outbox, and creates a blocked author stage.
The publication and prior evidence survive. Resume does not retry that writer.
`ResolveReworkBaseAsync` carries the immediate unintegrated candidate forward;
after integration, rework starts from current story state.

## Verification and its limits

Final local packages: SDK **4.3.2**, Technical Director **4.0.2**, Engineer **4.0.2**,
QA **4.0.0**. Agent identities, manifests, package metadata, pins and unpublished
release notes match. Agent tests/self-tests/pack consume the packaged SDK with
sibling SDK/work-contract references disabled. SDK tests use published work
contracts. Platform checks also disable sibling Office/Isolation references;
existing Memory source references remain necessary for this checkout.

- SDK: 427 tests, two HelloAgent tests, seven generated-template tests and template self-test pass.
- Technical Director: 126 tests and self-test pass.
- Engineer: 245 tests and self-test pass.
- QA: 84 tests and self-test pass.
- Platform: 129 focused tests pass, including amendment authorization/revision fencing, preview requirements, review schemas, integration/QA rework, runtime internet grants and preserved candidate selection.
- `AgentDeliveryJourneyTests` passes: production TD callback through the actual review tool schema; complete real Git diff paging with malformed fourth-page correction; trusted InternalGit task integration; production QA callback, snapshot hash checks, actual fixture command failure and canonical platform result validation; author rework, second review/integration, actual passing QA command and orchestration Done.

The connected journey scripts model responses and broker transport and uses human
completion as its dispatch boundary. Its authored fixture runs locally; it never
executes fetched game code on the host. It proves component handoffs, not installed
provider/grant/VM behavior, browser/device compatibility of NEON BREAK, or a live
hosted preview. Existing SDK tests cover interruption/resume, stale identities,
overflow and unresolved findings. Existing real Git tests cover lost promotion
responses and both pre/post-integration rework.

Run the optional cross-repository journey from the C-Sweet root with the TD and QA
checkouts at their documented sibling paths:

```powershell
dotnet test tests/CSweet.DeliveryWorkflow.Tests/CSweet.DeliveryWorkflow.Tests.csproj `
  -c FullPlanPackage2 -p:UseLocalCSweetAgentSdk=false `
  -p:UseLocalCSweetWorkManagementContracts=false -p:UseLocalOfficeContracts=false `
  -p:UseLocalIsolation=false `
  -p:RestoreAdditionalProjectSources=C:/Users/PC/Documents/GitHub/CSweet.Agent.Sdk/artifacts/full-plan-final
```

The test project is separate from the normal application solution because it
requires sibling production agent repositories. Its framework override matches
the QA harness dependency without upgrading the application framework.

## NEON BREAK rollout before retry

No live retry, scope mutation, merge, installation or grant change was performed
by this implementation. Read-only diagnostics still found the original blocked
task with planning revision 2 and the active scope-revision-1 delivery plan.

1. Publish the final SDK package and deploy/restart the platform broker/API changes before updating agents. Update Technical Director, Engineer and QA through the normal immutable package installation path; verify the active versions and SDK. Keep existing manager schedules OnDemand and the older YouTube Account Manager/Discord AlwaysOn. Check runtime timeout settings after synchronization, which may restore definition defaults.
2. Review/approve QA's new `runtime.internet.public.v1` request and npm registry access through normal installation grants. Confirm effective dependency restoration in its development guest; no source test grants that access.
3. Pause the plan, reread current plan/item/planning revisions, then submit the owner-approved task amendment. The local ignored evidence folder `artifacts/review-contract/completion-analysis/mvp-task-amendment.draft.json` contains the exact proposed MVP text and preserved source identities. Its observed revision is stale after pause and must be refreshed; do not submit it unchanged. It replaces Pending/null repository statements and GitHub-only CI requirements while retaining the engine/browser/creative requirements.
4. Resume the plan and explicitly retry the new blocked author stage only after installed-runtime smoke checks. Repair the preserved candidate: malformed URL handling, missing-asset status, application loading, consent behavior, provider-appropriate build/preview recipes and accurate readiness/feasibility documents need observed checks. Local platform tests do not certify those application fixes.
5. Require current technical approval, a trusted task integration receipt, and independent QA of that exact integrated story commit with all accepted criteria evidenced before Done. Unavailable browser/device evidence must remain an explicit manager decision or require the missing environment; it cannot become a pass. Then verify dependent task 12 becomes eligible. Story/release review and exact-source hosted preview readiness remain separate gates.
