# Game studio startup recovery

`ProjectApprovalReview.ReviewProjectAsync` keeps the Producer as delivery owner and the Director as business approver. Earlier live reviews requested an impossible ownership change while code correctly retained the Producer. Revised proposals discovered through attention now update the exact tracked proposal before approval, so a blocked original coordination session cannot hide a created project.

`ProjectProposalDraft.PrepareProjectDraftAsync` validates the complete descriptive metadata shape before caching or submitting. Extra `deploymentTargets` and `pickupInterpretation` fields caused repeated platform validation failures in the PRISM BREAK run. Bounded correction now fixes those drafts; valid command identities remain stable. Staffing-role authority can narrow, never expand.

The live leadership hires succeeded. Director 0.2.1 and Producer 6.0.1 were released and installed through Installed Agents. The existing PRISM BREAK business then created an approved project with the Producer as delivery lead.

A separate startup failure affected Naomi: `AgentTokenBudget.WithDefaults` adds platform-owned token budgets, but the specialist SDK configuration does not declare those fields. `AgentTokenBudget.ForRuntime` now removes only undeclared platform budget fields at the runtime boundary. `McpAgentSessionService.EstablishAsync` and `AgentInstallationConfigurationService.QueueRefreshAsync` share that projection and its matching digest. Full effective settings remain available to the UI, configuration validation and `PlatformLlmCapabilityHandler` budget enforcement. Declared agent budget fields are preserved; unrelated unknown fields are not silently suppressed.

Verification: Director 254 tests; Producer 458 tests including malformed metadata, cache recovery and authority subsets; two cross-role journeys; 30 host configuration/session/token-budget tests. Live completion of project creation was verified separately from unit tests. A created project alone is not evidence of a completed sprint or demo.
