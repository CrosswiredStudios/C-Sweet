# Model output contracts for agents

Rules for any agent step that asks a model for structured output and then submits it to the
platform. They exist so that small local models succeed, not only strong hosted ones.

## Why

Qwen Games, 2026-11-09, Producer 6.4.1 on `qwen3.8-flash-next`. The Director's project template
listed `game-producer` among the authorized staffing roles. The Producer prompt told the model to
"narrow it to roles justified by the approved scope". The model removed `game-producer`, reasoning
that the delivery lead was already hired. The platform requires every active profile staffing role
in the envelope and rejected the proposal. The Producer's local validator only checked that the
list was a subset of the template, so the draft passed, was cached as accepted, and every retry
replayed it. The call used 10,410 output tokens and 53 seconds.

The model did what it was told. The contract was wrong.

## Rules

1. **Code computes what code can compute.** If a field is fixed by a template, a profile, a
   policy or arithmetic, the model does not return it. Authority, identity, keys, dates, budgets
   and role lists are never model output.
2. **Ask for changes, not copies.** Do not ask the model to return a structure "exactly as given".
   Ask for the few values it should author and merge them into the template in code.
3. **Tell the model only rules it must apply.** If the model does not need a rule because code
   enforces it, leave it out of the prompt. Never give an instruction that conflicts with a
   platform rule.
4. **Validate with the platform's rules before caching.** The local validator must reject
   everything the platform rejects for that request, using shared code where it exists.
5. **Cache a command as accepted only after the platform accepts it.** A draft that passed local
   validation is not accepted.
6. **Return the platform's exact rejection to the model once.** One corrective generation, under
   its own cache key, then a terminal blocker that quotes the rejection.
7. **Heal on read.** When a release changes what code owns, apply that ownership to drafts and
   commands cached by earlier releases before dispatch.
8. **Give every terminal failure an owner who can act.** A message asking another agent to "check
   the validation error" is not ownership. State who retries and what must change first.
9. **Design recovery around what changed.** After a fix ships, the blocked step must be re-run by
   something. Prefer "the participating package version, schema digest or template digest changed
   since the blocked turn" over a rule that matches one error message.
10. **Record the failing reply as a test.** Each incident adds the model's actual output as a
    fixture, run through the same validation the platform applies.

## Review checklist for a prompt that requests JSON

- [ ] Every requested field needs judgment. No field could be copied or computed in code.
- [ ] No sentence asks the model to preserve, retain or exactly reproduce input.
- [ ] No instruction can be followed in a way the platform would reject.
- [ ] Extra or missing fields in the reply cannot change authority or identity.
- [ ] The corrective message names the actual failure.
- [ ] A platform rejection reaches the model once and cannot be replayed from a cache.
- [ ] A test feeds a weak model's real reply through platform-equivalent validation.
- [ ] Output is small: the reply does not re-emit the input.

## Applied

- Producer 6.4.2 `ProjectProposalDraft.cs`, `ProjectFoundation.cs`: staffing authority fixed to the
  template, milestone details merged in code, one platform-rejection correction, cached drafts and
  frozen commands healed before dispatch.
- Director 0.6.9 `GddSemanticValidation`: the model judges semantic coverage and selects
  supporting evidence; code derives finding codes, disposition and exact revision bindings.
  The reviewer gets one correction for invalid/unsupported evidence. GDD document layout
  remains unrestricted, and accepted state is saved only after platform acceptance is confirmed.

## Not yet applied

- Producer project metadata (`profileData`) is still copied back by the model and checked for
  exact shape.
- The proposal rules live only in `WorkstreamGovernanceCapabilityHandler.ProposePlanAsync`. Agents
  cannot run them before submitting.
- The generate, validate, correct-once loop is hand-written per agent. It belongs in the SDK.
- Other Director, Technical Director and Creative Director JSON prompts still need review
  against this checklist; the GDD semantic reviewer above covers only that operation.
