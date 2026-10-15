# Blocked tickets: where they go and how to retry them

## What happened (VGF943299B17-5)

Victor's specialist stage hit the old generation time limit twice. The second identical failure blocked the stage, the
item execution and the ticket, as designed. Three things then left the CEO with no way to restart it:

1. The ticket was told to move to the board's Blocked column, but the producer-created board had none, so the card
   stayed in **In Progress** and looked like it was still running.
2. Sprint cards could not be moved by hand at all ("transitioned only by the work orchestrator").
3. The Retry button in **Sprints → Execution** was refused: the retry endpoint only accepted the stage assignee or
   the board manager (the Producer), ignored the CEO's `work.orchestration.retry` grant, answered with a bare
   `Forbid()` (a cookie redirect, so the button appeared to do nothing), and the page echoed the sprint execution
   revision where the server expected the ticket's assignment revision.

## Behaviour now

- **Blocked work looks blocked.** When a sprint stage is Blocked (or Failed with its automatic retries spent), the
  orchestrator parks the card in the board's Blocked column on its next pulse. A board without one gets a "Blocked"
  column appended (`WorkBoardBlockedColumn.EnsureAsync`); repeated-failure blocking in `AgentTicketFeedback` uses the
  same helper. Dragging a card into a Blocked column now sets its status to Blocked.
- **Moving a blocked card back to a ready or working column retries it.** For a sprint card the board asks the
  orchestrator to retry the stage the ticket stopped at; for any other card the blocker, claim and waiting fields are
  cleared so its owner picks it up as fresh work. Moving a running sprint card by hand is still refused.
- **Retry button on the ticket.** Blocked or failed cards show **Retry** on the card and in the ticket drawer.
  It calls `POST /work/boards/{boardId}/items/{itemId}/retry` with the card revision, which is exactly "move it to the
  board's last To Do column".
- **Who may retry.** The stage assignee, the board manager, or a person holding the board's `work.orchestration.retry`
  grant. Agents stay limited to their own stage or the board they manage.
- **Attempt budget.** The budget bounds automatic retries only. Each manual retry grants one further attempt; if it
  fails the same way the stage stops again (the repeated-issue rule) and waits for the next decision.
- **Errors are visible.** Forbidden retries and moves return a JSON message instead of a redirect, stale revisions
  return 409, and the board shows the server's sentence rather than its JSON envelope.

## Tests

`tests/CSweet.UnitTests/BlockedTicketRetryTests.cs` reproduces the ticket: a board without a Blocked column, a stage
blocked by a provider timeout with all three attempts spent, and the CEO as the caller.

`WorkOrchestrator.RecordArtifactQualityAsync` selects the author attempt with the greatest `Attempt` number before
checking its status and document reference. EF navigation collections have no guaranteed order, and a returned
Blocked outcome also has attempt status Completed. Selecting the last completed collection entry can therefore
mistake an earlier blocked attempt for the successful delivery (observed on `VGF99E0D5782-6`). A newer unfinished
or invalid attempt must not fall back to an older document. `HierarchicalTaskRuntimeTests` covers both collection
orders, game and software authors, reviewed rework, and refusal to reuse an older delivery.

For this failure, deploy the platform correction and retry the current **quality** stage through
`WorkOrchestrationService.RetryItemAsync` or the ticket's Retry action. Preserve the completed author stage and its
submitted artifact revision; the QA retry receives that existing delivery along with the historical attempts.
