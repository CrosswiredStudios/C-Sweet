# C-Sweet Personal Assistant

Standalone protocol-v2 SDK agent, bundled with C-Sweet and installed once per user outside business employment. Version **0.1.0**, package ID com.csweet.personal-assistant, SDK package **3.66.0**.

PersonalAssistantAgent handles typed conversation and repository-analysis callbacks. IPersonalAssistantReasoner separates reasoning from callback validation and transport. PersonalAssistantReasoner uses AgentRuntimeContext.CreateChatClient; provider credentials, repositories, staging storage, business provisioning, and documents remain in the control plane.

The manifest provides personal-assistant.converse.v1, personal-assistant.analyze-repositories.v1, and the standard SDK configuration describe/update capabilities. Its sole required capability is platform.llm.chat-stream.v1. It requests no direct network access, credentials, business administration capabilities, or event subscriptions. Activation is OnDemand.

Configuration uses the same SDK builder and platform validation as other agents: required llmProviderId, llmModel, maxContextWindowTokens, and maxOutputTokens. The output budget must fit inside the context window. C-Sweet supplies reviewed user-scoped grants, encrypted durable work, runtime configuration, cancellation, and completion replay protection.

Repository analysis receives bounded excerpts with snapshot IDs, never GitHub credentials or executable workspaces. Responses include one project brief per source and evidence that must match supplied files. Unsupported facts remain empty or become questions for the user. The platform independently validates the output before allowing review and creation.

Run dotnet test CSweet.PersonalAssistant.slnx, dotnet run --project src/CSweet.PersonalAssistant -- --self-test, and dotnet pack src/CSweet.PersonalAssistant -c Release.

The platform embeds this project's manifest, build properties, and source files into its Infrastructure assembly, then uses PluginArchiveImportService, the ordinary signed agent build pipeline, and SDK runtime. This project has no reference to platform implementation assemblies and can be built and tested independently.
