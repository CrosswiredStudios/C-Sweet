# Agent model capacity

`PlatformLlmCapabilityHandler.StreamAsync` resolves the authenticated installation's saved
model output capacity through `AgentTokenBudget.ResolveOutput` on every call. Legacy
`PlatformChatRequest.MaxOutputTokens` hints cannot reduce or increase it. Provider-profile
output defaults cannot silently lower it. The platform sends the resolved value explicitly,
because omitting it can restore a smaller server default. SDK 5.12.1 stops forwarding task
output hints; the matching platform also protects agents using older SDKs.

Context window and maximum generation are model capacities, not arbitrary task budgets.
The current defaults are 256,000 total tokens and 128,000 generation tokens, including
reasoning. Configure these to match the actual serving model. This change does not invent
unlimited hardware capacity, change a model server's launch configuration, or silently edit
saved installation settings. Input and generation share the finite model window.

Producer 6.3.3 and Director 0.4.6 retain the full supplied pitch-refinement transcript and
use their configured output capacity. Producer manager chat retains all supplied history.
QA 5.5.4 removes its 65,536 context / 8,192 generation overrides and uses the same configured
capacity for execution and report finalization. Report handoffs and correction drafts no longer
have separate 64k/16k character cutoffs, and evaluation notes are retained in full. QA and both developer agents compact older
tool history only when the configured input space (context minus generation capacity) fills,
retaining accepted scope and recent tool groups. Smaller contexts still trigger compaction
at their actual capacity. The default broker message-count limit is disabled; explicit
administrator size limits, authority checks and cancellation remain supported.

SDK shared incident reporting retains all supplied current facts, historical evidence,
limitations and authorized exact document revisions. Corrective source selection sees all
supplied accepted requirements, criteria and findings, including decision-bearing tails.
Its existing quoted-evidence and single-source checks still reject unsupported mappings.
Reporting remains separate from corrective execution. Fixed three/five-minute generation
deadlines no longer override the caller's cancellation lifecycle in these shared calls.

`AgentRunLog.InferenceSettingsJson` records the same effective output capacity sent to the
provider. A length-stopped generation or one with no answer/tool call is recorded as Failed,
with `llm.output_incomplete` or `llm.empty_response`, preserving usage and partial evidence.
It cannot become a successful empty answer or trigger an automatic identical retry loop.

Regression coverage: `PlatformLlmCapabilityHandlerTests` exercises smaller/greater/absent
legacy hints, provider defaults, invalid saved capacities, 600-message history, reasoning-only
exhaustion, truncated text, successful text and successful tool calls. SDK transport and
long-evidence selection tests cover the agent boundary. Developer compaction regressions
exercise histories below the old threshold, above it but below capacity, and beyond capacity.
QA regressions exercise execution/report separation at configured capacity.

Rollout: publish SDK 5.12.1 before the affected agent releases, update through Installed Agents,
and restart C-Sweet with the matching platform build. Local packages do not update installed
agents. No data migration or direct runtime/database patch is needed.
