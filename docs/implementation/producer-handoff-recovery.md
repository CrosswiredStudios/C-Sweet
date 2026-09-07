# Producer handoff recovery

The reported run hired Creative Director 1.5.0 and Producer 2.2.0 successfully.
Read-only runtime and audit inspection showed the Creative Director's resource-decision,
workforce-change, hiring-fulfilled, and attention events failed three times and dead-lettered.
Every attempt stopped after reading the approved resource request. No Workstream or
coordination session had been created; the Producer's own attention event completed with
no managed project to process.

The next operation requested a scoped roster with pageSize 200, exceeding the MCP schema's
maximum 100. Creative Director 1.5.1 and Producer 2.2.1 correct those requests. The host also
allows an active team's lead's direct manager to read its roster before Workstream supervision
exists, without making the manager an ordinary team member. Organization boundaries and
active-lead checks remain enforced. The roster includes provided work capabilities alongside
authorized tool capabilities, preserving dotted capability names rather than filtering them
as role keys. This lets the handoff recognize the Producer's work.execution.run.v1 capability.

The current flow still requires project-plan approval before the accepted brief can be attached
to a Workstream and handed to the Producer. The Creative Director now reports that approval
wait explicitly. After the handoff, the Producer plans scope and requests technical leadership,
then proposes delivery roles against the backlog. The Creative Director reviews resource changes;
approved resource changes are materialized as Chief of Staff hiring suggestions.

Deploy the host fix and import Creative Director 1.5.1 and Producer 2.2.1. Existing installed
packages do not acquire these edits automatically. Resume the existing project through the
normal runtime retry/review controls after updating; retain the accepted vision, approved
staffing request, and existing Producer. Do not create replacement hires to recover the handoff.
No live approvals, messages, hires, or workflow records were altered during diagnosis.

Validation covers bootstrap supervisor access, unrelated/inactive lead denial, and work-capability
visibility, plus both agent suites. This is not a live end-to-end confirmation of hiring delivery.
