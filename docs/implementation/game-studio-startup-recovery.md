# Game studio startup recovery

`ProjectApprovalReview.ReviewProjectAsync` keeps the Producer as delivery owner and the Director as business approver. Earlier live reviews requested an impossible ownership change while code correctly retained the Producer. Revised proposals discovered through attention now update the exact tracked proposal before approval, so a blocked original coordination session cannot hide a created project.

`ProjectProposalDraft.PrepareProjectDraftAsync` validates the complete descriptive metadata shape before caching or submitting. Extra `deploymentTargets` and `pickupInterpretation` fields caused repeated platform validation failures in the PRISM BREAK run. Bounded correction now fixes those drafts; valid command identities remain stable. Staffing-role authority can narrow, never expand.

The live leadership hires succeeded. Director 0.2.1 and Producer 6.0.1 were released and installed through Installed Agents. The existing PRISM BREAK business then created an approved project with the Producer as delivery lead.

A separate startup failure affected Naomi: `AgentTokenBudget.WithDefaults` adds platform-owned token budgets, but the specialist SDK configuration does not declare those fields. `AgentTokenBudget.ForRuntime` now removes only undeclared platform budget fields at the runtime boundary. `McpAgentSessionService.EstablishAsync` and `AgentInstallationConfigurationService.QueueRefreshAsync` share that projection and its matching digest. Full effective settings remain available to the UI, configuration validation and `PlatformLlmCapabilityHandler` budget enforcement. Declared agent budget fields are preserved; unrelated unknown fields are not silently suppressed.

Verification: Director 254 tests; Producer 458 tests including malformed metadata, cache recovery and authority subsets; two cross-role journeys; 30 host configuration/session/token-budget tests. Live completion of project creation was verified separately from unit tests. A created project alone is not evidence of a completed sprint or demo.


Creative review continuation: `VideoGameCreativeDirectorAgent.ValidReview` and `VideoGameDirectorAgent.ValidCreativeReview` preserve `changes-required` with `requiresDirector=true` when concrete findings and required changes exist. `ReviewProducerPitchAsync` resolves those questions as the lead; it cannot accept the disputed draft unchanged. The observed PRISM BREAK response was valid rework with a manager question, not an authorization mismatch.

`RecoverIncompleteCreativeReviewAsync` rereads the child session, exact approved pitch, current project and manager before a bounded SDK coordination resume. `HandleManagerAttentionReviewAsync` performs child recovery before onboarding/reporting/portfolio reconciliation. `CheckProducerHandoffAsync` treats the expected creative-review wait as a dependency while retaining real terminal failures. Director 0.2.4 and Creative Director 4.0.2 carry this repair. Tests include a downstream failure after recovery, wrong authority, stale pitch, substantive blockers, retry exhaustion, and a cross-role journey using the observed mixed rework/Director-question response.

`CanResumeCreativeParent` (Director 0.2.5) also binds completion wakes to the parent's latest authenticated Producer brief revision. A completed review of an older draft cannot repeatedly resume a newer draft that is still waiting for its own independent review.
