# Personal Assistant and repository business onboarding

## Entry points and boundaries

`Organizations.razor` opens `NewBusinessDialog` from Enterprise. Fresh businesses retain the existing name/industry/mission flow. `RepositoryBusinessOnboarding` prepares an existing app, game, or website from multiple GitHub repositories and repository ZIPs. `/personal-assistant` is a separate user dashboard with guidance, saved draft links, `PersonalAssistantSettingsForm`, and `PersonalAssistantChat`.

The agent lives in `agents/CSweet.PersonalAssistant`, builds independently against SDK 3.66.0, and uses the normal configuration builder and `AgentRuntimeContext.CreateChatClient`. It receives no direct repository access, credentials, or business-management authority. `BundledPersonalAssistantInstaller` embeds its source and installs a server-marked bundled package through `PluginArchiveImportService` and `IBundledAssistantInstallationService`. Build jobs, signing, isolation, work inbox, LLM broker, and configuration hydration follow the existing agent pipeline.

`PluginInstallationScope.User`, `AgentInstallation.OwnerApplicationUserId`, and `PersonalAssistantProfile` bind an installation to one registered user. `UserScopedAgentPolicy` requires an enabled, active, server-marked bundled assistant and matching profile. `AgentRuntimeEligibilityService` still requires a built, signed package and execution capacity. LLM dispatch verifies that scope on every request. Ordinary agents still require active employment. The generic agent-install API cannot create a user-scoped assistant.

`PersonalAssistantService` exposes per-user settings and chat. Saves go through `AgentInstallationConfigurationService`, including provider/model and token-budget validation, revisions, and normal runtime refreshes. First-run setup requires an explicit assistant provider/model selection; completed installations retain their setup status. Chats use encrypted `AgentWorkInbox` payloads/results, stable request keys, bounded history, and persisted pending messages.

`Setup.SetupSteps` / `SetupService.RequiredSteps` place the `personal-assistant` step between provider connections and optional media. `PersonalAssistantSetupStep` saves through `PersonalAssistantSettingsForm` before completing that step. New forms default to 256,000 context tokens and 128,000 output tokens; existing values are preserved. `PersonalAssistantProfile.Name` stores the user-facing name (default `AI Assistant`), with name-only edits included in the configuration revision check. `FirstRunSetupGuardMiddleware` permits the authenticated assistant settings endpoint during setup, while assistant messages and repository onboarding remain guarded. `PersonalAssistantApiClient` displays readable ProblemDetails messages rather than raw JSON.

## Draft lifecycle

`RepositoryOnboardingService` authorizes every draft read/mutation against `ApplicationUserId`. Revisions reject stale changes. `Sources → Preparing → Analyzing → Review → Creating → Created`; unsuccessful stages become `AnalysisFailed` or `CreationFailed`. Only failed stages may be retried. Source changes clear previous analysis/review. Manual review can supersede analysis; late results cannot overwrite it. Discarding clears excerpts and removes staged archives. A partially created business must be managed from Enterprise.

GitHub authorization uses a hashed, expiring nonce scoped to the draft. `IGitHubUserAuthorizationClient.VerifyInstallationAsync` proves that the OAuth user can access the returned installation. Available accounts are limited to installations verified for that draft. Selection and final association recheck provider access and cross-business conflicts; per-repository locks serialize final attachment. GitHub repositories remain in place.

`RepositorySourceStore` streams uploads into a user/draft store, validates using `WorkspaceArtifactValidator`, removes an optional ZIP wrapper, and preserves normalized files for import. GitHub snapshots retain actual paths. Traversal, links, `.git`, expanded-size and file-size violations are rejected. Analysis excludes dependency/generated folders, binary files, credential filenames, private keys, and detected credential assignments. Bounded excerpts include coverage warnings. Code/builds are not executed; submodules are not fetched. This conservative filter is not a complete secret scanner; users explicitly approve sending excerpts to their chosen model.

`RepositoryOnboardingWorkflow` saves source checkpoints, queues SDK analysis, and owns creation. `RepositoryBriefValidator` checks bounds, complete source coverage, and evidence against actual paths/snapshots. Users edit company/project summaries and approve associations before creation. The combined brief must fit the canonical 128 KiB document limit.

Creation uses stable `BusinessOnboardingService.StartAsync` and `ArtifactDocumentService.CreateAsync` keys, plus reserved repository IDs. `InternalGitRepositoryStore.ImportAsync` verifies the content hash, bypasses filters/hooks/builds, creates one initial commit, and atomically writes the branch and receipt. Replay reads `refs/csweet/imports/<hash>` directly, including after a lost successful response. Another archive cannot replace an existing repository. No project, team, or Chief is automatically staffed. Creation hands off to the Chief of Staff marketplace with the reviewed profile and full brief in Documents.

## Recovery and operations

`PersonalAssistantResultObserver` applies matching authorized completions before the inbox save, with notification outbox records in the same transaction. Duplicate/late completions are harmless. `ApplicationRealtimeOutboxItem.RecipientApplicationUserId` routes to the owning user group. Events carry hints; UI components reload authorized state on events/reconnect, preserving newer revisions and unsaved review edits.

`RepositoryOnboardingWorker` is platform queue maintenance, not an agent polling loop. It claims persisted work, recovers missed terminal results, cancels expired assistant work, and wakes built installations with pending work. Partial creation preserves checkpoints for retries.

Apply migration `PersonalAssistantAndRepositoryOnboarding` through the normal deployment migration process. Update GitHost and Core together for `/internal/v3/onboarding/snapshot` and `/internal/v3/onboarding/import`. Configure a durable absolute `RepositoryOnboarding:StagingPath` shared by processing Core replicas. The default is `data/repository-onboarding` under the app directory. Drafts persist until discarded; completed archives are retained for recovery.

Apply the additive `PersonalAssistantDisplayName` migration for assistant names; existing profiles receive `AI Assistant`. Adding the onboarding step does not reopen completed installations.

## Verification map

- Standalone agent tests: SDK configuration, model selection, budgets, malformed input, manifest, denied business access.
- `RepositoryOnboardingTests`: archives/isolation, GitHub paths, evidence, late results, standard bundled install/config/runtime, replay-safe chat/import, canonical business/document handoff after a lost import response.
- `PersonalAssistantRenderingTests`: actual dialog/dashboard/source controls in Aurora and Gold.
- `SetupServiceTests`: explicit assistant model requirement and preservation of completed installations.
- `AssistantSetupTests`: settings access before setup, complete save payloads, saved values, validation, discovery/load/save recovery, and prevention of advancing without a successful save.
