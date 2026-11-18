# Planning stalls: which agents, why, and a shared project state

November 17, 2026. Analysis for review. Nothing in sections 5 to 7 is implemented.

## 1. Question

Planning still does not complete on a fresh business. Which agents are the pain point, and would it help if agents knew more about the phase or state the project is in?

**Short answer.** The Producer (Gabriel) is in every stall. The Technical Director (Victor) is in three. Nearly all of them happen in the handoff between the two. Phase awareness would have prevented three of this week's failures. The facts the agents needed existed, but as private state inside one agent's code, where neither the other agents nor the model could read them.

## 2. Evidence

Eight stall episodes across seven fresh businesses, November 15 to 17. Episodes 1 to 3b are from the earlier run reviews in `run-reviews/`; I did not trace them myself. Episodes 4 to 7 I traced from live records and code.

| # | Business | Where it stopped | Agent whose step failed | What failed |
|---|---|---|---|---|
| 1 | QFN Brickfall, Nov 15 | Staffing, before a Technical Director was hired | Gabriel | Kept a stale wait after the Director returned his staffing proposal |
| 2 | Qwen Prism Break, Nov 15–16 | Admitting the first Story | Gabriel | Review rejected because resources changed during the model call; a consultation routed to himself |
| 3a | QFN Shardfall, Nov 16 | Estimates | Gabriel | Estimate field rejected by the platform; reused request key |
| 3b | QFN Shardfall, Nov 16 | Sprint 1 active, document in review | Victor and Gabriel | Victor's document rejected six times; author and reviewer did not share the same evidence |
| 4 | QFN Prism Break, Nov 16 | Admitting the first Story | Gabriel | Could not see Victor's draft; asked the platform to reopen a resolved issue; crash was never released |
| 5 | Qwen Prism Break, Nov 17 00:16 | Choosing the first Story | Gabriel | Ownership review reported a missing engineer during the hire; review asked to select from an empty list |
| 6 | QFN Brickstorm, Nov 17 10:03 | Roadmap | Victor, then Gabriel | Victor's commitment list rejected one item at a time; Gabriel blocked permanently |
| 7 | Qwen Prism Break, Nov 17 12:38 | Refining the first Story | Victor, then Gabriel | Victor's task list rejected with a message that named no problem; Gabriel escalated instead of asking again |

No run has completed a planned sprint. Only Shardfall started one.

| Agent | Episodes | Role in the failure |
|---|---|---|
| Gabriel, Producer | 8 of 8 | Owner of the step that stopped, or the one who turned a collaborator's failure into a stop |
| Victor, Technical Director | 3 of 8 | His model's output was rejected by a validator |
| Marcus, Director | Most | Received blocks he had no means to fix, and repeated "recovery" |
| Naomi, Hana, Daniel, QA | 0 | Planning rarely reached them |

## 3. The agents

### Gabriel, Producer

He is not one role. His agent does six jobs:

1. Chooses what to build next and publishes it.
2. Audits whether every approved obligation has an owner.
3. Proposes and corrects staffing.
4. Collects estimates and capacity.
5. Prepares and starts the sprint.
6. Writes incident reports for his manager.

Size as of today: 60 source files, about 9,600 lines, 7 distinct model judgments, and 51 places that return Blocked (after about ten were rerouted to retry this week). His planning commitment runs these jobs in one pass, in series. Any one of them stopping stops the others.

Why this matters for small models: each judgment is asked inside a large prompt with index bookkeeping, and the instructions for several of them are accumulations of rules added after individual incidents.

### Victor, Technical Director

His role is clear and his scope is reasonable: 33 files, about 3,800 lines. His failures are all one kind. He generates plans one page at a time, 13 page types in all, each with three attempts, and each checked by a validator. Until this week the validators reported one problem per attempt, or one combined sentence for several rules. A small model that is converging runs out of attempts, the page fails, and the page failure fails the whole request. Three of these have been fixed individually (commitment citations, constraint-only Epics, task lists). The rest have not been reviewed.

His failure message also told every reader to escalate to the CEO for a code correction. Gabriel's planning review read that and obeyed it.

### Marcus, Director

He is the escalation target and has no tool that can repair what reaches him. In episodes 4 and 5 his recovery commitment looped on a block that only a code change or a release could clear. In episode 7 he was alerted about a failure that was already being retried.

### Naomi and the brief

Not yet examined, and upstream of everything. The accepted brief for a Breakout clone was split into 428 numbered sources and produced 42 commitments, half of them constraints. Roadmaps have opened with a governance package, then a feasibility spike, and have included evidence, audit and review packages as Epics. Every downstream step carries that weight, and a small model's error rate rises with it.

## 4. Where phase awareness was missing

| Episode | What the agent did | What it did not know |
|---|---|---|
| 5 | Gabriel's ownership review reported "no engineering implementer" and held the first Epic | The hire was 34 seconds from completing; staffing was in progress |
| 7 | Gabriel's planning review escalated Victor's failure and held the increment | A retry of that exact request was already scheduled |
| 4, 5 | Marcus repeated manager recovery on Gabriel's commitment | Code had already decided the commitment could not continue without a release |

In each case the fact was recorded, but in one agent's private state: a counter in Gabriel's planning cycle, a hire in the staffing records, a block reason in a personal to-do. Nothing presents "where this project is and who has the next move" as one thing every agent reads.

So the diagnosis is narrower than "agents need more awareness". The models reason adequately about the situation they are shown. They are shown a partial one.

## 5. Proposal: one shared project state

A single record per project, owned by the platform and shown on the board, that every agent reads at the start of a turn.

| Stage | Owner | Output that ends the stage | If the step fails |
|---|---|---|---|
| Brief accepted | Director | Accepted planning package | Returned to Creative Director with the reason |
| Roadmap | Technical Director | Epics on the board | Retried; then Producer narrows the request |
| Story selected | Producer | A named Story and why it is first | Retried |
| Refining | Technical Director | Story and task briefs | Asked again with the reason |
| Review | Producer | Publish, or a named correction | Retried |
| Published | Producer | Story and tasks on the board | Retried |
| Staffing | Producer, Director approves | Every task has an eligible owner | Proposal revised; scope narrowed |
| Estimating | Task owners | Estimates and capacity | Asked again |
| Sprint ready | Producer | Preflight passes | Waits on the named preflight error |
| Active | Team | Work claimed | Existing execution recovery |

Each record carries: current stage, owner of the next move, what is being waited on, attempt number and next retry time, and open side items (ownership gaps, pending hires, pending decisions) marked as not blocking unless they are.

What this changes:

- **Agents coordinate on the same fact.** Gabriel's review sees "Refining, attempt 2, retry at 12:41" and does not escalate. The ownership review sees "Staffing in progress: engineer" and does not report it. Marcus sees who owns the next move and whether anything is being asked of him.
- **Code owns transitions; models own judgments inside a stage.** A model is asked "is this Story good enough to publish?", never "what should happen next in the project?".
- **Stalls become visible as a stage that has not advanced**, with an owner and a reason, in one place the CEO can read.
- **Each stage is a test scenario**: a known input, an owner and an expected output. This is the shape the agent testing framework needs.

This replaces the step split planned earlier. Splitting one method into steps without a shared state would have moved the private state around, not exposed it.

## 6. Proposal: narrower roles

Following the stated preference for real-world roles and small scopes:

| Today | Proposed |
|---|---|
| Gabriel does all six jobs | Gabriel plans: selects, reviews and publishes Stories, and manages staffing proposals |
| Ownership audit runs inside Gabriel's planning pass | A separate step that only opens issues, on its own commitment |
| Estimates and sprint preparation run inside Gabriel | A sprint-preparation commitment of its own, or a Scrum-master-style role if a hire is warranted |
| Incident reports written by a model for every failure | Retried failures are status. Only a spent retry budget or a needed decision is an incident |

Victor's role stays. His page validators get one standard: report every problem at once, name the rule and the offending item, and repeat earlier corrections.

Whether sprint preparation becomes a new agent or stays a separate commitment of Gabriel's is a design choice. A new agent costs a hire and another handoff. A separate commitment gets most of the isolation at lower cost.

## 7. What this does not fix

- **The brief.** If scope is inflated upstream, planning will reliably plan the wrong things.
- **Model judgment quality.** A small model will still choose poorly sometimes. Shared state limits the damage; it does not improve the choice.
- **Execution.** The Shardfall review loop is after planning and is untouched.
- **Unknown failures past the first Story.** No run since the refactor has reached estimates. Problems there are still undiscovered.

## 8. Suggested order

1. Finish the pending Producer fix so the current business can move: re-send a Story request directly after a failure, and report a retried failure as status.
2. Examine the brief: where the 428 sources and the evidence Epics come from.
3. Design the shared project state: record shape, who writes each transition, how agents read it. Review before any code.
4. Apply the validator standard across the Technical Director's remaining page types.
5. Move the ownership audit and sprint preparation out of Gabriel's planning pass, onto the shared state.
6. Build the testing framework's first scenarios from the stage table.

## 9. Decisions needed

1. Is the shared project state a platform record that agents read, or an agent-maintained document? A platform record is recommended, because agents are untrusted and the state gates work.
2. Does sprint preparation become a new agent or a separate commitment?
3. Is the brief's scope what the CEO asked for, or should the Creative Director and Director be constrained?

## 10. Current state of the code

- Agent SDK 5.40.0, Producer 6.22.1 and Technical Director 6.13.3 are committed. Technical Director 6.13.3 passes its 256 tests.
- The Producer change in step 1 is not written.
- The live Qwen Games business is cycling every 15 minutes on "escalate and hold". It will not ask Victor again until that Producer change is installed.
