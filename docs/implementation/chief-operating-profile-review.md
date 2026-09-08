# Chief of Staff operating-profile review

Chief package 2.4.0 introduces a required inference step after hiring and before the existing focus phase. It assesses authoritative company facts against the installed Business Operating Profile. Appropriate or ambiguous fits skip directly to focus; clear mismatches present the current mode, a reason, a preset switch, and Leave unchanged. Custom remains an installation choice and is never inferred as a suggested replacement.

The assessment is persisted in installation-scoped agent operating state under its onboarding event ID. Inference failures are retryable and cannot seed the leadership agenda. A profile decision must be attached successfully before the lifecycle event is acknowledged. The next phase waits for the user's choice.

`platform.user-input.request.v1` accepts an optional `configurationChange` object with `key`, `currentValue`, and `proposedValue`. The platform validates a declared select field and canonicalizes the card with the actual field and preset labels. These cards expose `allowFreeText: false`; the API also rejects free-text answers. Existing decisions keep free-text support. The existing OptionsJson column stores a version-compatible envelope for action metadata; readers still accept legacy arrays.

Only an active human business owner may answer a configuration card. Applying checks the current effective value and saves a sparse employee override via IAgentConfigurationService, preserving unrelated overrides and refreshing the runtime. Leave unchanged performs no configuration write and uses the latest effective value. Configuration and answer persistence, the user message, and exact-installation event delivery share the database transaction. PostgreSQL serializes simultaneous answers with a decision row lock.

Both choices emit `com.csweet.agent.configuration-choice.answered.v1` with decision, organization, conversation, field, effective value, selected option, and actor identity. The Chief resumes the existing focus phase from this trusted event without another inference. Message, decision, and personal-work idempotency keys prevent duplicate phase side effects on retries.

Rollout requires the platform update plus importing Chief 2.4.0 with operating-state read/write grants and the configuration-choice event subscription. No SDK or shared package contract changes are required.

## Ask-tool delivery and selectable questions (2.4.1)

The Chief prefers `ask_user` for every necessary question when it is available, including clarifications, confirmations, and relayed Product Manager choices. Ordinary requests omit the optional configurationChange property. Required focus-question failures must propagate without acknowledging onboarding.

Decision creation, supersession, answers, and cancellation now produce a durable `com.csweet.communication.decision.updated.v1` event. This refreshes an already displayed message after its choice card is attached, without requiring a new message or page reload.

A running pre-profile-review AgentHost rejects the profile question with `JSON Schema validation failed: $.configurationChange is not allowed`. Restart the updated AgentHost and API before importing Chief 2.4.1. Importing a new package version allows the failed onboarding outbox entry to deliver again with its original event and message keys; it does not require the user to type a replacement answer.
