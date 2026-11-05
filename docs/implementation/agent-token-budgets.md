# Installation ceilings and task output budgets

`PlatformLlmCapabilityHandler.StreamAsync` resolves the authenticated installation's saved
output ceiling through `AgentTokenBudget.ResolveOutput`. A positive per-call
`PlatformChatRequest.MaxOutputTokens` may reduce the output budget for that task, but may
never increase the installation ceiling. Omitted and nonpositive legacy hints retain the
configured budget. Provider-profile defaults do not silently reduce it.

The same effective value is sent in `ChatOptions.MaxOutputTokens` and recorded in
`AgentRunLog.InferenceSettingsJson`. Invalid installation budgets still fail before invoking
the provider. `PlatformLlmCapabilityHandlerTests.StreamAsync_HonorsSmallerTaskBudgetsWithinTheAgentCeilingWithoutProviderCaps`
covers smaller tasks, larger requests, absent/legacy hints, provider defaults and invalid saved
configuration.

QA 5.5.0 already requests an 8,192-token output budget for code validation and uses a
65,536-token harness context ceiling. Live verification showed the older platform replacing
that request with the saved 128,000-token maximum. This platform correction honors the
existing QA request; no additional QA release is required. It loads on the next normal
C-Sweet restart. Keep active execution uninterrupted until a suitable restart boundary.
