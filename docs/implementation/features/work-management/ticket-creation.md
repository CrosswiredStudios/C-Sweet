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
