# Creating tickets with parent and member assignments

`WorkBoards.OpenItemCreator` loads the current board policy, active people and enabled
installations in the business. The new-ticket form exposes a parent from the currently
loaded board and explicit human or agent choices for `MemberExecution` stages. No
member is selected automatically. Starting a sprint remains a separate action.

`WorkBoards.SetStageMember` retains exactly one principal kind. Switching between a
human and an agent clears the previous choice. `BuildItemStageAssignments` rejects
missing or ambiguous member choices and retains the existing manual, agent, manager
approval and trusted platform stage handling.

`CreateItemAsync` sends `ParentItemId` and the selected `WorkStageAssignment` records
through the existing `CreateBoardWorkItemRequest` endpoint. `WorkBoardService` still
validates parent compatibility, business membership, installation authority, ownership
and published-policy assignments. Form selections do not create grants, approve a
candidate or authorize source execution. Server validation failures remain visible.

`WorkBoardItemCreationTests` exercises exact principal selection, switching/clearing,
invalid selections and the actual request's parent and assignment fields.

The ticket drawer exposes the existing software-developer repository brief action to
board members with update authority. `SoftwareDevelopmentWorkService` loads the current
development-stage principal before binding a brief. A governed assignment must match that
exact agent and provide `work.execution.run`; it requires and grants only the workspace
read/comment/Git operations. It does not add legacy item start/complete authority.
Assignments without a development stage retain their legacy lifecycle requirements.
Both paths still require the confined development runtime, serialized jobs, current team
repository policy and the existing platform authorization checks. `DeveloperBriefAuthorityTests`
covers incompatible principals, missing capabilities, runtime restrictions and legacy behavior.

`WorkItemMutationEngine.ClaimAsync` distinguishes personal plans on project boards from
canonical stage tickets. Outside a personal board, a retained `PersonalWorkContextJson`
is required for new claims, claim replay and expired-claim recovery. Assigning a developer
or repository brief alone cannot enter the personal dispatcher. Canonical tickets retain
their stage-owned execution and claim state. `PersonalTodoClaimBoundaryPostgresTests`
reproduces ready, replayed and expired canonical claims and preserves ordinary personal
plan claims and revoked-grant checks against PostgreSQL.
