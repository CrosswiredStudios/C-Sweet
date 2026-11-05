# Uniform provider response handling

## Shared contract

`OpenAiCompatibleLlmProviderFactory.AdaptChatClient` wraps
`ReasoningContentChatClient` with `ProviderOutputLimitChatClient` and
`ProviderResponseContractChatClient`. Every
provider accepted by `LlmProviderTypeExtensions.UsesOpenAiCompatibleApi` uses the
same wrapper: LM Studio, Unsloth Studio, Ollama, vLLM, OpenAI-compatible, OpenAI,
Gemini's compatible endpoint, OpenRouter, Groq, Together AI, Meta Muse, DeepSeek and Custom.
Native APIs outside that supported set still require their own adapters; a
uniform agent contract does not mean every provider or model has equal capability.

`ReasoningContentChatClient` preserves assistant reasoning history at the wire
boundary without changing tool-call/result pairs. `UserQueryChatClient` supplies
a missing user message where the configured compatibility policy requires it.
Provider output defaults retain explicit caller budgets.

`SetupWizardState.HostedProviderPresets` supplies Meta Muse at
`https://api.meta.ai/v1` and DeepSeek at `https://api.deepseek.com` for setup and
provider settings. Both require an API key and use model discovery rather than
fixed model names. The same compatible client handles connection/capability tests
and agent requests. See the [Meta API reference](https://dev.meta.ai/docs/api-reference)
and [DeepSeek API guide](https://api-docs.deepseek.com/).

`LlmProviders.PreviewModelCatalogAsync` fills the Default Chat Model dropdown
with the complete returned catalog. The user chooses a model; refresh preserves
that choice while it remains available. Changing connection details clears the
catalog and selection, and older in-flight responses cannot replace newer results.
Saved profiles read their catalog with the stored credentials through
`ILlmProviderApiClient.GetModelCatalogAsync`.

## Bounded recovery

`ProviderOutputLimitChatClient` treats configured output budgets as ceilings rather
than a reservation. It sends the full configured ceiling first, without estimating
prompt tokens, preflight model discovery, or reducing input. If the provider returns
HTTP 400 before any response update and explicitly identifies an oversized output
ceiling with exact input/context counts, it retries once with the remaining output
space. A server-suggested smaller allowance takes precedence, including required
headroom. Supported error formats are the Cool Games server's `at most ... here`
response and vLLM's current/legacy output-limit validation messages. Unrecognized
errors and input-only overflow propagate; no guessed fallback is applied.

The retry preserves messages, instructions, reasoning history, tools, schema and
other options. It clones the per-call options, leaves saved installation/provider
limits unchanged, and passes through the normal transport authorization again.
Cancellation, a second rejection, or any started stream prevents further retry.
No model output is replayed. Logs contain only requested/applied numeric ceilings;
the configured ceiling remains in the broker's `InferenceSettingsJson`, while
provider-reported usage records actual consumption. Successful first requests do
not incur an extra inference call. A generation reaching its adjusted ceiling
retains the provider's normal finish reason and result validation.

`ProviderResponseContractChatClient.GetResponseAsync` and
`GetStreamingResponseAsync` accept usable answers, structured tool calls and other
non-reasoning content. Reasoning alone or an empty completion receives exactly
one corrective request with the original history, selected model, instructions,
tools and output options. The corrective user message asks for structured calls
or a final answer, without replaying the unusable assistant response.

Responses that already contain usable output or a structured tool call are never
replayed by this recovery. Transport errors and cancellation propagate. Reasoning
text is never parsed into an executable call, and the normal harness/platform
authorization checks continue to own execution. Provider-specific chat templates
and tool parsers remain the server's responsibility; see
[LM Studio's tool-use contract](https://lmstudio.ai/docs/developer/openai-compat/tools).

When `LlmProviderProfile.SupportsStreaming` is false, the wrapper requests a normal
completion and converts it to the same agent update stream. A provider without
SSE support can therefore participate without changing agent code.

## Failure and accounting

After the corrective request, `LlmResponseContractException.FailureCode` is
`llm.tool_protocol` for tool-call syntax appearing only in reasoning with tools
available, or `llm.response_invalid` for other unusable responses. Diagnostics
contain approved explanations, never provider bodies or reasoning text.
`LlmProviderFailureMessage`, `PlatformLlmCapabilityHandler.StreamAsync` and
`PlatformLlmJobService.ReadAsync` preserve that code across direct streaming and
queued polling. These failures are not classified as transient, so generic
availability recovery does not repeat the same formatting problem indefinitely.

The streaming wrapper retains only the last cumulative usage snapshot per
generation and emits one combined usage record for the logical request. Both
generations count once, including known usage on a failed corrective stream.
Missing provider usage remains unknown. Per-request state and bounded marker
tails prevent cross-conversation state sharing or retaining a second full copy
of reasoning.

## Verification

`ProviderResponseContractChatClientTests` covers repair, bounded failure,
preservation of scope/tools, cancellation, no replay after tool output, usage and
nonstreaming fallback. `ReasoningContentChatClientTests` exercises JSON/SSE wire
fixtures through the actual SDK adapter. `OpenAiCompatibleLlmProviderFactoryTests`
checks that every supported provider receives the wrapper. Broker and queue tests
verify failure-code propagation and persisted usage. These are protocol fixtures,
not certification against live versions of every provider.

`ProviderOutputLimitChatClientTests` exercises the real SDK JSON/SSE paths,
nonstreaming fallback, the observed server rejection, unchanged successful
requests, exact limit adaptation, option/history preservation, usage, bounded
failure, input overflow, malformed/unrelated errors, cancellation, dispatch
reauthorization and no replay after metadata, reasoning, text, tools or usage.

November 4 Cool Games verification: 171 focused provider, broker, dispatch and token
budget tests passed; one existing optional live-stream test was skipped. A separate
synthetic live probe linked the production adapter source and called the configured
server: a 128,000-token output ceiling received HTTP 400 for a 6,060-token prompt
and 131,072-token context. The adapter retried at the server's explicit 125,004-token
allowance, received HTTP 200 and `OK` with 36 output tokens and finish reason `stop`.
The original `ChatOptions.MaxOutputTokens` remained 128,000. Evidence is in
`artifacts/dynamic-output/results/dynamic-output.trx` and
`artifacts/dynamic-output-live/probe.log`. Builds used isolated outputs and published
SDK/work/Office contracts. This verifies adapter recovery, not Marcus's business
pitch: no running service was restarted and no business work was retried.
