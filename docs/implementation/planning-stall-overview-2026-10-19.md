# QwenGames planning stall and Overview issues

## Verified incident

Read-only queries against the running QwenGames business on October 19, 2026
confirmed two open, escalated incidents for project `1f72cf46-e96a-4034-8bd7-0627381f845f`
(working title: “1. Working title and player promise”):

- Collaboration `22019294-8d66-4028-bf8a-34a5068daf8e` is Blocked.
- Producer commitment `d989df30-c879-4218-ae70-3267d9be9890`, “Reconcile scope,
  technical backlog and delivery staffing”, is Blocked because that planning failed.

These are two reports of one failure. Their project, board, initiating employee and
planning-cycle artifact key match exactly. They are not two independent stalled agents.
Both incidents were detected at approximately 8:30 p.m. Pacific on October 19.
Installed versions were Technical Director 5.0.1 and Producer 4.0.6.

The Technical Director exhausted three validation attempts on zero-based scope
coverage page 1. The final response was 274,970 characters against the 128,000-character
guard and reported `finish=stop`. This establishes completed but unacceptable output,
not a provider token-exhaustion finish or a confirmed connectivity failure. Run
`902a722a-ba07-4ecd-b61f-0476699ceff9` used `muse-wonder-1.0-contributor`.
Its recorded provider is Meta Muse (`MetaMuse`), whose configured endpoint is
`https://api.meta.ai/v1`, not the local Qwen/vLLM endpoint. The recent local Qwen
BF16-to-FP8 change therefore does not explain this particular response based on
the retained routing evidence; no provider settings were changed during investigation.
Its effective saved inference budget was 128,000 output tokens. Usage was not reported
for that final run. The individual earlier validation errors are not preserved in the
final coordination diagnostic; a 4,356-token intermediate response does not establish
that every rejected attempt exceeded the character guard.

### Full-response follow-up

Reconstructing the three responses from their retained, organization-scoped audit chunks
established the concrete cause of the excessive size: repetition inside `manifestDigest`,
not a huge scope mapping. The final reconstructed length exactly matches the runtime
diagnostic, so this is not an artifact of the diagnostic reconstruction.

| Attempt/run | Characters | Observed failure |
| --- | ---: | --- |
| `aaf7a938-6b98-4246-8b6f-d11b8a928709` | 237,638 | The digest value begins at character offset 2,722. The remaining 234,916 characters repeatedly copy the hash or its suffix; the JSON string never closes. |
| `95ac2f83-646e-4c84-98e0-e7d7a3881a65` | 2,951 | Valid JSON with twelve distinct source mappings, but the digest is 99 characters rather than the expected 64: the correct hash followed by a repeated suffix. |
| `902a722a-ba07-4ecd-b61f-0476699ceff9` | 274,970 | The digest value begins at character offset 2,217. The remaining 272,753 characters are a repetition loop in that string (99.19% of the response), with no closing quote. |

The expected manifest digest is already computed by trusted agent code. The general
generated schema declares a string but does not constrain it to that exact value or
length. Asking the model to reproduce an opaque hash, allowing an unconstrained string,
and dispatching with the much larger saved output budget allowed this failure to grow.
The final size guard correctly rejected it before publishing invalid planning.
The retained output proves the repetition; it does not establish the provider's internal
reason for reporting `stop` on an unfinished JSON string or for omitting final usage.

Operating state retains the manifest, outline pages 0–4 and coverage page 0 under
`technical-progressive:c1ec0a668c3a785443df9a94bad2e37a48175dbaa713b4d0f3183c2102d5be62`.
Coverage page 1 and the completion marker are absent. Saved planning is recoverable;
there is no evidence here of a completed outline, activated sprint or delivered game.

## Why the interface was misleading

- `ProjectHealthService.ReadAttentionAsync` required owner decision input. Technical
  generator failures correctly require no creative decision, so the Overview hid them.
- `AppRealtimeCoordinator.ShowNotification` suppressed `ProjectHealth` but allowed
  `ProjectHealthStatus`. Technical escalation uses that latter category.
- `ProjectHealthService.PresentAsync` embedded the nested specialist diagnostic in its
  summary, making each snackbar repeat the same long failure and management routing.

## Implemented platform changes

- `ReadAttentionAsync` also returns delivered, currently failing escalations as status
  cards. Authorization, current-recipient filtering and current failure checks remain
  mandatory. Stale owner decisions and bounded manager recovery remain separate.
- `TechnicalPlanningFailureAsync` binds a commitment to its collaboration only with the
  exact organization, project, board, initiating employee and planning artifact key.
  Ambiguous matches remain separate. Authorized reports for the same exact cycle share
  one card and a related-report count. Stored incidents and audit history remain intact.
- `ProjectIncidentAttentionCard` shows the issue summary, recorded recovery status,
  next step, investigation/report links and collapsed technical evidence. It continues
  to offer revision-bound, idempotent retry for eligible nontechnical collaborations.
  A known generator defect offers investigation and explains correction before retry.
- `PresentAsync` keeps planning summaries concise. Full diagnostics stay in retained
  evidence and the card's expandable details. `ShowNotification` suppresses both health
  categories, including replayed status notifications.

## Proposed robustness work, in priority order

The first fix should remove model-generated opaque metadata. Bind coverage pages to the
trusted manifest and requested source set in code, rather than asking the model to copy
a 64-character digest. If the wire shape must retain a model-returned digest, constrain
the provider schema to the exact expected value (`const` or a single-value enum) instead
of an arbitrary string. Preserve deterministic coverage/source validation either way.

1. **Bound the actual coverage response shape.** Technical Director
   `ProgressiveOutlinePages.ReadOrGenerateScopeOutlineAsync` requests twelve sources
   per coverage page, but uses a general `OutlineCoveragePage` schema without page-specific
   array limits or source/story identity enums. Supply exact allowed source identities,
   exact mapping count, bounded unique Story owners and a fixed manifest digest in the
   provider schema; retain deterministic semantic validation after generation. Test
   repetition, unknown/duplicate sources and missing scope. A schema constrains shape;
   it cannot prove that a requirement is mapped correctly.
2. **Reconcile task ceilings with installation budgets.** Technical Director
   `SpecialistAgent.ResponseOptions` requests a 32,000-token planning ceiling. Broker
   `PlatformLlmCapabilityHandler.StreamAsync` deliberately replaces per-call caps with
   `AgentTokenBudget.ResolveOutput` from saved configuration; the incident used 128,000.
   Define and test an explicit task-budget contract, usually the minimum of a validated
   task ceiling and the configured installation ceiling. This changes the existing
   documented budget policy and its regression tests; do not silently substitute it.
   Character and token limits are different units, so a token ceiling alone cannot
   guarantee the 128,000-character/checkpoint boundary.
3. **Adapt recovery instead of repeating the same oversized page.** After a size failure,
   split the remaining source batch into smaller checkpointed units with deterministic
   identities. Reuse validated pages, preserve every approved source and recheck scope
   digests before resuming. Handle failure between subpages, duplicate delivery, restart
   and cancellation. Never truncate JSON or omit accepted requirements to satisfy limits.
4. **Retain concise structured failure facts.** Record phase, page/source identities,
   attempt, finish reason, effective budget and response character count separately
   from explanatory prose. Keep individual attempt validation codes in diagnostics.
   Bind the collaboration and dependent commitment to one root failure identity when
   recording them; this avoids relying on prose or inference during later presentation.
5. **Verify the real handoff before claiming recovery.** Exercise installed provider
   transport, checkpoint resume, final outline validation, Producer wake/reconnect and
   authoritative sprint readiness. Publish any changed agent packages and apply them
   through Installed Agents updates. Then resume the existing planning cycle through
   normal authorized recovery, preserving accepted documents and validated pages.

The platform UI changes do not fix or resume the planner. This investigation did not
change live records, installed agent packages, permissions or runtimes. No development
deployment was performed.

## Verification

Regression coverage is in `NotificationSnackbarTests`, `ProjectIncidentAttentionCardTests`
and `ProjectHealthTests.TechnicalPlanning`, with the existing project-health,
dashboard and attention tests. It checks status-only cards, no fabricated decisions,
collapsed evidence, guarded retries, exact-cycle deduplication, ambiguous-cycle separation,
recipient/organization isolation and removal when authoritative work completes.

The final `IncidentOverviewVerified` build and focused run passed 277 tests with
zero failures or skips, including executive decisions, pending questions and Overview
rendering/realtime recovery. `git diff --check` passed. The tests use existing sibling
dependency references; no published package or installed runtime was changed.
