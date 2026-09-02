# Legacy, Transitional, and Dead-Code Audit

> **Superseded historical audit.** Its file counts and transport findings describe the pre-cutover
> codebase. Use the [MCP-only migration](../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md)
> and [current architecture](../../Documentation/Architecture/MCP_AGENT_RUNTIME.md) for current decisions.

**Audit date:** 2026-07-19

**Scope:** Production code, tests, project files, migrations, application assets, and current architecture documentation. Generated `bin`/`obj` content and vendored library contents were excluded from text searches.

## Executive summary

Yes—there are several places where the proof of concept is carrying an older approach alongside a newer one. The most important issue is not the small amount of obviously dead code; it is that the agent-to-plugin migration is only partially complete. The code currently has two authorization vocabularies, two management APIs, two sets of service interfaces, an old wire protocol behind a new facade, and more than one model-execution path.

The highest-value cleanup is:

1. Finish the typed-capability security model and remove the legacy permission fallback.
2. Decide whether the plugin model is the canonical runtime model, then collapse the parallel agent/plugin APIs and interfaces accordingly.
3. Decide whether model execution must go through brokered agents. If so, remove the in-process planning runner and direct-provider chat fallback rather than allowing alternate behavior to hide broker defects.
4. Squash the 36 proof-of-concept migrations into a clean baseline after the model cleanup.
5. Remove the verified dead code, disabled tests, unused packages, and roughly 9.5 MB of unreferenced Bootstrap assets.

No customer or production-database compatibility requirement was found that justifies preserving these transitional layers. Some developer databases may need to be recreated after the recommended migration squash.

## Priority map

| Priority | Finding | Why it matters |
| --- | --- | --- |
| P0 | Typed capabilities and legacy permissions conflict | New installations can be denied access by checks that they cannot satisfy through the current install API. This is both baggage and a correctness/security risk. |
| P1 | Agent and plugin platforms are additive duplicates | Every new feature can accidentally extend only one of two public surfaces or dependency-injection abstractions. |
| P1 | Multiple model-execution paths remain | Planning and chat can bypass the brokered runtime, producing different security, memory, isolation, and failure behavior. |
| P1 | 36 migrations encode ten days of discarded schema history | There are no live databases, so this is the ideal time to create one truthful baseline. |
| P2 | Legacy routes, stream aliases, setup reconciliation, and legacy constraints remain | These are explicit compatibility mechanisms with no customer state to protect. |
| P2 | Dead source, disabled tests, and unused abstractions remain | They increase search surface and make future intent harder to infer. |
| P3 | Project/package/asset residue remains | Low risk individually, but easy to remove and currently causes warnings, build ambiguity, and unnecessary repository weight. |

## Detailed findings

### 1. P0 — The authorization migration is internally inconsistent

The current plugin manifest and installation path use typed required capabilities, but parts of the broker still enforce legacy string permissions:

- Import previews always populate `RequestedPermissions` with an empty array and populate the new `RequestedCapabilities` property instead (`src/CSweet.Infrastructure/Setup/AgentImportPreviewService.cs:295-320` and `src/CSweet.Infrastructure/Setup/PluginArchiveImportService.cs:131-140`).
- Installation explicitly rejects every non-empty `GrantedPermissions` request (`src/CSweet.Infrastructure/Setup/AgentInstallationService.cs:104-110` and `:413-419`).
- The generic broker routing check nevertheless accepts a legacy `capability.request` permission when no typed requested capabilities exist (`src/CSweet.AgentHost/Broker/AgentSessionRegistry.cs:269-275`).
- The platform LLM handler requires `capability.request` (`src/CSweet.AgentHost/Broker/PlatformLlmCapabilityHandler.cs:41-44`).
- The platform memory handler also requires `capability.request`, then requires additional permission strings for individual memory actions (`src/CSweet.AgentHost/Broker/PlatformMemoryCapabilityHandler.cs:48-50` and `:227-230`).
- Newer handlers already use the typed model correctly—for example communications, workforce, HTTP, and WebSocket handlers check `RequestedCapabilities` (`src/CSweet.AgentHost/Broker/CommunicationHubCapabilityHandler.cs:31-33`, `WorkforcePlatformCapabilityHandler.cs:39-41`, `PlatformWebProxyCapabilityHandler.cs:36-38`, and `PlatformWebSocketCapabilityHandler.cs:28-31`).
- The UI and contracts still carry the dead permission approval surface (`src/CSweet.Contracts/Agents/InstallAgentRequest.cs:3-18`, `AgentImportPreviewResponse.cs:5-34`, and `src/CSweet.UI/Pages/Agents.razor:527-533`).

**Recommendation:** Make `manifest.requires`/`RequestedCapabilities` the only plugin-facing authorization vocabulary. Update LLM and memory handlers to authorize their exact capability names. If memory needs finer scopes, model those explicitly as capabilities or a separate typed data-scope policy rather than retaining generic legacy permissions. Keep internal broker privileges such as installation routing out of the public plugin grant contract. Then remove `RequestedPermissions`, `GrantedPermissions`, `PermissionsJson`, the `capability.request` bypass, and their UI/tests/migration columns.

This should happen before other cleanup because it determines the correct contracts and final database shape.

### 2. P1 — The agent-to-plugin transition created parallel APIs and interfaces

The newer plugin layer is explicitly additive rather than canonical:

- `IPluginImportService`, `IPluginBuildExecutor`, `IPluginRuntimeManager`, and `IPluginContainerRunner` inherit the older agent interfaces (`src/CSweet.Application/Setup/PluginAbstractions.cs:5-14`).
- Dependency injection registers the same concrete implementation under both names (`src/CSweet.Infrastructure/DependencyInjection.cs:84-106`).
- Both `/api/agents` and `/api/plugins` are mapped (`src/CSweet.Api/Program.cs:149-150`). They duplicate preview, install, list, get, enable, disable, update, remove, run-list, build-log, and configuration operations in `AgentManagementEndpoints.cs` and `PluginManagementEndpoints.cs`.
- The shared DTOs exposed by `/api/plugins` still have agent names such as `PreviewAgentImportRequest`, `InstallAgentRequest`, `AgentInstallationResponse`, and `AgentInstallationException` (`src/CSweet.Api/Agents/PluginManagementEndpoints.cs`).
- The UI consumes both APIs: `AgentApiClient` uses `/api/agents`, while `PluginApiClient` and communication setup use `/api/plugins` (`src/CSweet.UI/Services/AgentApiClient.cs`, `PluginApiClient.cs`, and `src/CSweet.UI/Pages/CommunicationProviderSetup.razor:70-76`).
- `CSweet.Plugin.SDK` is a facade over `CSweet.Agent.SDK`, translating plugin registration into `RegisterAgent` and other agent-protocol messages (`src/CSweet.Plugin.SDK/PluginBrokerClient.cs:18-47`). Twenty-six production files still directly reference the old SDK/protobuf transport.

**Recommendation:** First decide the domain boundary:

- If every executable extension is a plugin and an agent is a plugin kind, rename the canonical domain/services/contracts to plugin terminology and keep agent-specific behavior only where it is genuinely agent-specific.
- If agents and service plugins are intentionally different products, stop using inheritance aliases. Define the shared runtime primitives once and compose separate agent/plugin application services over them.

In either case, expose one management API with one DTO vocabulary. Because there are no external API consumers, now is the inexpensive time to make the breaking change. Replace the old wire facade with a native plugin protocol in the same change or explicitly document the old transport as a temporary milestone with a removal issue and deadline.

### 3. P1 — There are multiple model-execution architectures

The repository has a newer brokered/container runtime, but older direct execution remains active:

- `PlanningRunService` still injects the in-process `IAgentRunner` (`src/CSweet.Infrastructure/Planning/PlanningRunService.cs`). The duplicate `PlanningDocumentService` identified by this audit has since been retired in favor of collaborative artifacts.
- `AgentFrameworkAgentRunner` calls `ILlmProviderFactory`/`IChatClient` directly rather than using the brokered agent runtime (`src/CSweet.AI/AgentFramework/AgentFrameworkAgentRunner.cs:11-58`).
- DI registers that direct runner alongside the runtime and planning services (`src/CSweet.Infrastructure/DependencyInjection.cs:146-152`).
- The chat path first dispatches to an agent, but on failure invokes the model provider directly and deliberately bypasses memory capture (`src/CSweet.Api/Chat/ChatTurnWorker.cs:265-304`). The fallback has its own two-minute timeout (`src/CSweet.Api/Chat/ChatTurnOptions.cs:8-14` and `src/CSweet.Api/appsettings.json:14`).

This gives the same product two security, observability, memory, isolation, retry, and tool-execution behaviors. During a proof of concept, the direct fallback can also conceal broken broker/runtime integration by returning a plausible answer.

**Recommendation:** If brokered plugins are the newest intended architecture, move business-strategy planning behind a real agent/plugin capability and remove the in-process runner. Disable the direct-provider fallback by default during the proof of concept; keep only a deterministic visible failure. If a production resilience fallback is desired later, add it intentionally with a feature flag, explicit product semantics, and equivalent governance rather than preserving it as an alternate architecture.

If direct in-process execution is actually preferred, make that the canonical decision and delete the container/broker path that it supersedes. Maintaining both without a crisp boundary is the baggage.

### 4. P1 — The migration history should be squashed before any real database exists

There are **36 migrations** dated from July 9 through July 19, 2026 under `src/CSweet.Infrastructure/Persistence/Migrations`. Several exist only to transform states that no customer database needs:

- `20260714043000_UpgradeAgentRuntimeImagesToNet10.cs` updates old persisted image names.
- `20260716162349_AddAgentEmployeeMemory.cs`, `20260717035707_AddExtensiblePluginPlatform.cs`, `20260717162139_AddImmutablePluginInstallationRevisions.cs`, and `20260717004554_AddDiscordManagedWorkspace.cs` contain data-copy/update SQL.
- `20260719184607_ConsolidateAgentCommunications.cs` represents another transitional consolidation.

The designer files and snapshot repeat the full model many times and make schema review disproportionately difficult.

**Recommendation:** After findings 1–3 determine the final model, delete the proof-of-concept migrations and generate one new `Initial` migration from the final `CSweetDbContext`. Recreate all developer databases/volumes. Record the reset in the README so nobody mistakenly tries to upgrade an old local database. Do not squash again once a database that matters is deployed.

### 5. P2 — Explicit legacy UI routes are unnecessary without external bookmarks

`src/CSweet.UI/Pages/LegacyRouteRedirect.razor:1-24` exists only to redirect six old routes to the current settings hierarchy. `tests/CSweet.UnitTests/BusinessNavigationTests.cs:56-71` then locks those routes into the test suite.

**Recommendation:** Delete `LegacyRouteRedirect.razor` and remove the legacy-route assertions. Update any repository documentation or internal links to the canonical routes. There are no customers whose bookmarks need preserving.

### 6. P2 — The durable chat implementation still aliases the pre-turn stream identifier

`ChatStreamRouter` maintains a conversation-ID-to-turn-ID alias dictionary (`src/CSweet.Api/Chat/ChatStreamRouter.cs:7-52`). `ChatTurnWorker` binds the alias before dispatch (`src/CSweet.Api/Chat/ChatTurnWorker.cs:159-163`), and the gateway still accepts chunks with an empty `TurnId` and publishes them by conversation ID (`src/CSweet.Api/Chat/ApiGatewayBrokerWorker.cs:161-175`). The corresponding test explicitly calls this legacy behavior (`tests/CSweet.UnitTests/ChatStreamRouterTests.cs:8-19`).

**Recommendation:** Require `TurnId` in the agent response contract, reject missing IDs, publish only by turn ID, and remove `BindAlias`, `UnbindAlias`, `_aliases`, and the legacy test. This makes concurrent/retried turns safer and leaves one identity model.

### 7. P2 — Business constraints still use a legacy catch-all field and parser

The newer `BusinessProfile` stores structured profile fields, but constraints still live in `Organization.ConstraintsJson`. The workforce handler calls `ReadLegacyConstraints`, accepting either an array or an older object with a `constraints` property (`src/CSweet.AgentHost/Broker/WorkforcePlatformCapabilityHandler.cs:81-91` and `:457-461`). `PlanningRunService` also passes the raw JSON through as text. The legacy field remains in create/update/response contracts.

**Recommendation:** Add one canonical typed/JSONB `Constraints` collection to `BusinessProfile`, update all readers and writers, and remove `Organization.ConstraintsJson` plus its compatibility parser. Fold the database change into the new baseline migration.

### 8. P2 — Setup seeding preserves obsolete steps

`SetupService.EnsureSeededAsync` reconciles the current setup-step list against existing records and marks removed steps as optional instead of deleting them (`src/CSweet.Infrastructure/Setup/SetupService.cs:42-72`). That is upgrade behavior for old databases.

**Recommendation:** With a resettable proof-of-concept database, either seed the exact current list into a fresh database or delete no-longer-defined rows. If setup steps are code-defined and not user-authored, consider deriving them directly from code rather than persisting their definitions at all; persist only completion state keyed by the current definitions.

### 9. P2 — Verified dead production abstractions and placeholder framework code

The following production types have no production consumer:

- `IAgentWorkflowRunner`/`AgentFrameworkWorkflowRunner` are registered, but are used only by an integration test; no application service resolves them (`src/CSweet.Application/Llm/IAgentWorkflowRunner.cs`, `src/CSweet.AI/AgentFramework/AgentFrameworkWorkflowRunner.cs`, and `src/CSweet.Infrastructure/DependencyInjection.cs:147`).
- `AgentFrameworkAgentFactory` is used only by unit tests (`src/CSweet.AI/AgentFramework/AgentFrameworkAgentFactory.cs`).
- `AgentFrameworkToolRegistry` describes itself as an empty future placeholder and is used only by unit tests (`src/CSweet.AI/AgentFramework/AgentFrameworkToolRegistry.cs`).
- `StartChatTurnRequest` is declared but never referenced (`src/CSweet.Contracts/Core/ChatTurnContracts.cs:5`).
- `ICommunicationProvider`, `IWorkspaceProvisioner`, `IWorkspaceReconciler`, and `IExternalIdentityProvider` have no implementations or consumers. The active communication path is `ICommunicationPluginClient` (`src/CSweet.Communications.Abstractions/CommunicationContracts.cs:53-79`).

**Recommendation:** Delete these types and their tests now. Reintroduce an abstraction when there are at least two real consumers or a real substitution boundary. Keep the communication DTO records that the active plugin client uses.

### 10. P2 — Test doubles live in the production AI assembly

`FakeAgentRunner`, `FakeLlmProviderFactory`, and `FakeChatClient` are under `src/CSweet.AI` but are referenced only by tests (`src/CSweet.AI/AgentFramework/FakeAgentRunner.cs` and `src/CSweet.AI/Providers/Fake*.cs`). `LlmProviderPresets` is also only referenced by tests; the actual UI has a separate setup-preset model (`src/CSweet.AI/Providers/LlmProviderPresets.cs`).

**Recommendation:** Move reusable fakes into the test projects or a test-only support project. Delete `LlmProviderPresets` if the UI setup presets are the canonical source; otherwise make one production preset source and consume it from the UI/API rather than testing an otherwise unused class.

### 11. P2 — A stale test file is hidden from compilation

`tests/CSweet.UnitTests/CSweet.UnitTests.csproj:24-27` explicitly removes `PersonalAssistantAgentTests.cs` from compilation. The file references a `CSweet.Agents.PersonalAssistant` implementation that no longer exists in this repository and contains stale code that would not currently compile (`tests/CSweet.UnitTests/PersonalAssistantAgentTests.cs`).

There are also two template smoke tests: the unit test only asserts `true`, and the integration test has an empty body (`tests/CSweet.UnitTests/UnitTest1.cs` and `tests/CSweet.IntegrationTests/UnitTest1.cs`).

**Recommendation:** Delete the excluded personal-assistant test file and its `Compile Remove`; delete both `UnitTest1.cs` files. Excluding abandoned tests is worse than deleting them because it makes repository search results look supported when they are not.

### 12. P3 — Startup contains an unused worker and a no-op registration call

- `src/CSweet.WorkerHost/Worker.cs` is the default one-second logging loop from the worker template. It is never registered in `src/CSweet.WorkerHost/Program.cs`; the host registers the real workers instead.
- `AddAgentManagement()` does nothing but return the service collection (`src/CSweet.Api/Agents/AgentManagementEndpoints.cs:17-20`), yet API startup calls it (`src/CSweet.Api/Program.cs:26`).

**Recommendation:** Delete `Worker.cs`, remove `AddAgentManagement()`, and remove its startup call.

### 13. P3 — Central package management and Infrastructure contain unused references

The following centrally versioned packages have no `PackageReference` in any project:

- `Grpc.Core.Api`
- `Grpc.Tools`
- `Microsoft.Agents.AI`
- `NetCord.Hosting`

They are declared in `Directory.Packages.props` but unused. In particular, the code called “AgentFramework” uses `Microsoft.Extensions.AI`; it does not reference `Microsoft.Agents.AI`.

The isolated build also produced `NU1510` for `Microsoft.Extensions.Http` and `Microsoft.Extensions.Hosting` in `src/CSweet.Infrastructure/CSweet.Infrastructure.csproj:21-22`, indicating that the framework reference already supplies them.

**Recommendation:** Remove the four unused central versions and the two redundant Infrastructure package references, then restore/build to confirm the dependency graph.

### 14. P3 — The two solution files have already drifted

Both `CSweet.sln` and `CSweet.slnx` are committed, but neither describes the complete direct project set:

- `CSweet.sln` omits `CSweet.AgentHost`.
- `CSweet.slnx` omits `CSweet.Migrator` and `CSweet.Communications.Abstractions`.
- The README tells developers to restore/build `CSweet.sln` (`README.md:200-203`).

Some omitted projects are still built transitively, which makes the inconsistency easy to miss.

**Recommendation:** Choose one solution format, include every intended project directly, update the README/CI, and delete the other solution. If `.slnx` is the desired .NET 10 direction, make it canonical.

### 15. P3 — Unreferenced template assets add roughly 9.5 MB

The web host contains 44 Bootstrap JavaScript files/maps totaling approximately **8.68 MB** under `src/CSweet.App/wwwroot/lib/bootstrap`, but `src/CSweet.App/wwwroot/index.html` loads only MudBlazor and the shared UI CSS/JS. The MAUI host contains another two unreferenced Bootstrap CSS files totaling approximately **0.82 MB** under `src/CSweet.Maui/wwwroot/lib/bootstrap`.

Other template residue includes:

- `src/CSweet.App/wwwroot/images/weather.json`, with no reference.
- MAUI's `dotnet_bot.svg` and `AboutAssets.txt`, with no application reference.
- Default MAUI application IDs, icons, splash assets, publisher placeholders, and a mojibake dismiss glyph in `src/CSweet.Maui/wwwroot/index.html:24`.

The MAUI project itself is intentional according to `docs/implementation/00-architecture-baseline.md`; the finding is the template residue, not the host.

**Recommendation:** Delete the unused Bootstrap directories and sample assets. Replace the remaining MAUI template identity/branding when the mobile host becomes an active deliverable, or remove the host from the default solution build until then.

### 16. P3 — The PWA manifest is misspelled and therefore not served at the referenced path

The web page requests `/manifest.webmanifest` (`src/CSweet.App/wwwroot/index.html:14`), but the committed file is named `manfiest.webmanifest`.

**Recommendation:** Rename the file to `manifest.webmanifest`. This is a small active bug rather than compatibility baggage.

### 17. P3 — One unit test depends on the assembly being under the repository root

`CommunicationRouterTests.CoreProjects_DoNotReferenceTheDiscordImplementation` searches upward from the test assembly location for the repository. It failed when the audit intentionally placed build outputs in `C:\tmp`, even though the other 239 unit tests passed (`tests/CSweet.UnitTests/CommunicationRouterTests.cs:76-111`).

**Recommendation:** Inject or discover the repository path from a stable test input (for example, an MSBuild-generated constant or copied marker path) rather than assuming the output directory is nested under the checkout. This is not legacy support, but it makes isolated/CI build layouts brittle.

## Compatibility-looking code that should not be removed merely because of its name

The following items are not legacy baggage in the same sense:

- `PluginManifestReader` rejects `csweet-agent.json` with a clear upgrade error (`src/CSweet.Infrastructure/Setup/PluginManifestReader.cs:10-16`). Once the old format has no users, the special error can become a generic invalid-manifest error, but rejecting unsupported input is preferable to silently accepting it.
- “OpenAI-compatible” provider support is an active cross-provider API strategy, not backward compatibility.
- The owner fallback in management briefings is product resilience, not a legacy implementation.
- Aspire and Docker Compose serve documented developer-inner-loop and self-hosted-distribution roles. They are not duplicate legacy deployment paths unless that product decision changes.
- Database migrations as a mechanism should remain. Only the pre-release historical chain should be reset once, before databases matter.

## Recommended cleanup sequence

1. **Write the canonical runtime decision:** plugin vs. agent boundaries, one public management API, one broker protocol, and one authorization vocabulary.
2. **Fix authorization first:** migrate LLM/memory checks to exact typed capabilities; separate internal broker privileges; delete public legacy permission fields and bypasses.
3. **Collapse the additive platform:** remove alias interfaces/DI registrations and duplicate endpoints/clients; rename surviving DTOs and entities consistently.
4. **Choose one execution path:** broker planning work; disable/remove the direct provider fallback and unused in-process workflow layer if brokered execution is canonical.
5. **Normalize current data models:** move constraints to the canonical business profile, require turn IDs, and simplify setup-step persistence.
6. **Generate a fresh database baseline:** delete the 36 historical migrations, generate one initial migration, and recreate all development databases.
7. **Apply mechanical cleanup:** dead tests/types, template worker/no-op method, packages, assets, manifest typo, and one canonical solution.
8. **Add guardrails:** build the canonical solution in CI, treat new compiler/NuGet warnings as failures, and add an architecture test that prevents legacy permission fields or `/api/agents` from returning after the cutover.

## Validation and limitations

- Reference searches covered production code, tests, project files, configuration, and documentation. Candidate dead public types were individually traced before inclusion; generated migrations were excluded from dead-type counting.
- `dotnet build CSweet.slnx --no-restore` reached the projects but could not complete because currently running API, worker, UI, and Visual Studio processes locked normal output assemblies. This was an environment lock failure, not a compiler failure.
- A second isolated-output run compiled the complete unit-test dependency graph. Result: **239 passed, 1 failed, 0 skipped**. The sole failure was the repository-root path assumption described in finding 17.
- The existing uncommitted runtime-related workspace changes were treated as user-owned and were not modified by this audit.
- Reflection-only or externally consumed public APIs can evade static reference searches. No evidence of such consumers was found, and the proof-of-concept/no-customer premise makes removal reasonable, but each cleanup batch should still compile and run tests before commit.
# Superseded historical audit

> This audit is retained as historical evidence and names components removed by the MCP-only cutover. See [MCP-only migration](../../Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md) for current status.
