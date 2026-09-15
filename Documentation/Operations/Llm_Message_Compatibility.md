# Compatible-provider user messages

Some local chat templates (including the observed vLLM Qwen template) reject requests without a user-role message. Framework summarization or compacted histories can contain only instructions and assistant/tool messages.

The compatible provider adapter inserts a neutral user turn after leading system/developer messages only when no user turn exists. Existing instructions, messages, tool calls, and tool results retain their content, roles, and order. Both streaming and non-streaming requests use this adapter.

`CSweet:Llm:Compatibility:EnsureUserMessage` overrides the default for all providers. `CSweet:Llm:Compatibility:Providers:<provider-profile-guid>:EnsureUserMessage` overrides that value for one provider. Defaults enable this behavior for local runtimes, Custom, and OpenAiCompatible profiles; other profiles retain their original behavior. Set the relevant value to `false` to disable it. These are host configuration settings and require host reload/restart.

This compatibility setting does not change model token budgets or retry provider errors blindly.
