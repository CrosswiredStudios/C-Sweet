# Uniform provider response handling

## Shared contract

`OpenAiCompatibleLlmProviderFactory.AdaptChatClient` wraps
`ReasoningContentChatClient` with `ProviderResponseContractChatClient`. Every
provider accepted by `LlmProviderTypeExtensions.UsesOpenAiCompatibleApi` uses the
same wrapper: LM Studio, Unsloth Studio, Ollama, vLLM, OpenAI-compatible, OpenAI,
Gemini's compatible endpoint, OpenRouter, Groq, Together AI and Custom.
Native APIs outside that supported set still require their own adapters; a
uniform agent contract does not mean every provider or model has equal capability.

`ReasoningContentChatClient` preserves assistant reasoning history at the wire
boundary without changing tool-call/result pairs. `UserQueryChatClient` supplies
a missing user message where the configured compatibility policy requires it.
Provider output defaults retain explicit caller budgets.

## Bounded recovery

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
