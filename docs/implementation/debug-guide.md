# Debug & Local Development Guide

For disk reclamation after clearing the database, use the
[Windows local reset guide](local-reset.md). `scripts/Reset-CSweetLocal.ps1` previews
runtime/VM storage and optional generated builds, user data and the Aspire database
before an explicitly requested destructive reset.

## Prerequisites

- Windows with the .NET 10 SDK specified by `global.json`.
- Docker Desktop with its Linux container engine running. AppHost provisions PostgreSQL through Docker, so the complete application cannot start when the Docker engine is unavailable.
- An OpenAI-compatible model endpoint for AI features.
- Windows Professional, Enterprise, or Education with hardware virtualization for local untrusted-agent execution. The browser onboarding flow guides Hyper-V and RuntimeHost preparation.

Docker runs trusted development infrastructure. It is not the isolation boundary for imported or marketplace agents; those agents remain disabled until the certified hardware-isolation provider is ready.

## Quick Start

### Visual Studio restore errors with sibling repositories

`Directory.Build.props` selects local Agent SDK, Memory, WorkManagement Contracts, Office
Contracts and Isolation projects when their sibling checkouts exist. Visual Studio can report
`NU1105` when a selected dependency is outside the loaded solution or its restore graph is stale.
From the C-Sweet repository, run `dotnet restore CSweet.slnx`, then reload the solution.

For a solution that explicitly loads the selected dependencies, run:

```powershell
powershell -File scripts/New-LocalSolution.ps1
```

Open the generated `CSweet.Local.slnx` and keep its **local dependencies** folder's projects
loaded. `New-LocalSolution.ps1` evaluates the normal conditional MSBuild project graph and includes
only its actual project dependencies; it does not change package pins or dependency selection.
The generated solution is machine-specific and ignored by Git. Regenerate after changing sibling
checkouts or dependency switches. Continue using `CSweet.slnx` for portable command-line builds.

When Visual Studio is building or cleaning Debug outputs, use a separate configuration for
command-line verification:

```powershell
powershell -File scripts/New-LocalSolution.ps1 -AdditionalConfiguration MemoryDevelopmentVerified
dotnet build CSweet.Local.slnx -c MemoryDevelopmentVerified --no-restore
```

Run the normal restore first. A separate configuration gives verification its own build outputs;
avoid concurrent restores that change dependency switches or the package cache.

Before evaluating memory changes interactively, complete a build and use a disposable development
database with the migrations listed in `features/agent-memory-hardening.md`; compilation alone
does not apply those migrations or prove runtime acceptance.

### Option 1: Aspire AppHost (Recommended)

For the most guided Windows experience, double-click `Start-CSweet.cmd` in the repository root. It checks the .NET SDK and Docker engine, attempts to start Docker Desktop when it is installed but stopped, waits for the engine, and then starts AppHost.

Run `CSweet.AppHost` to start all services together with the Aspire dashboard.

**In VS Code:**
1. Set startup project: Right-click `src/CSweet.AppHost` → "Set as Startup Project" (or use `.vscode/launch.json`)
2. Press F5 or click Run → Start Debugging

**From terminal:**
```powershell
dotnet run --project src/CSweet.AppHost
```

This will:
- Build and start `CSweet.Api` on a random port
- Build and start `CSweet.App` (Blazor frontend) on a random port
- Build and start `CSweet.WorkerHost` as a background service
- Build and start `CSweet.AgentHost` as an unprivileged project process that applies policy and brokers approved agent operations
- Provision PostgreSQL as trusted infrastructure through Docker Desktop
- Open the Aspire dashboard automatically (shows all services, health status, logs)

The Aspire dashboard URL appears in the console output (`https://localhost:17201` or `http://localhost:15201` with the checked-in AppHost profiles).
Docker Desktop must be running because PostgreSQL is a required AppHost resource. AgentHost no longer launches untrusted agents as Docker containers. On Windows, untrusted execution uses the separately installed RuntimeHost service and a certified Hyper-V guest.

### AppHost debug fails with port 22201 already in use

`src/CSweet.AppHost/Properties/launchSettings.json` assigns port `22201` to Aspire's
resource service. An existing AppHost started from a terminal or another build
configuration can still own that port when Visual Studio starts a new debug session.
Separate build configurations isolate build outputs; running AppHosts still share the
checked-in development ports.

Identify the listener before stopping anything:

```powershell
Get-NetTCPConnection -LocalPort 22201 -State Listen |
    Select-Object LocalAddress, LocalPort, OwningProcess
```

Stop the earlier C-Sweet run using Ctrl+C in its terminal or Stop Debugging in its
IDE, then stop the failed debug session and press F5 again. Check the identified
process before terminating an orphaned verification run; do not stop all .NET
processes. The AppHost dashboard (`15201`/`17201`), telemetry (`21201`), and frontend
(`5097`) may also be occupied by that same run.

### Option 2: Individual Projects

Run any project independently for focused debugging.

```powershell
# API only (health endpoint on default port)
dotnet run --project src/CSweet.Api

# Blazor frontend only
dotnet run --project src/CSweet.App

# Worker background service only
dotnet run --project src/CSweet.WorkerHost
```

## External Services and Host Features

| Capability | Requirement | Notes |
|---|---|---|
| Complete application startup | Docker Desktop and its Linux container engine | AppHost provisions the required PostgreSQL database as a container |
| AI features | OpenAI-compatible model endpoint | LM Studio, Ollama, vLLM, or a compatible hosted provider |
| Local untrusted agents on Windows | Hyper-V, RuntimeHost, signed guest image, and current certification | Prepared through the guided Agent Isolation onboarding flow; never replaced by Docker |

## Inference request size configuration

### Provider concurrency

Open **Settings → LLM Providers**, select a provider, and edit **Maximum concurrent requests**.
`LlmProviderProfile.MaximumConcurrentRequests` defaults to **4** for new and migrated profiles;
the first-run provider editor exposes it too. This limits agent inference per saved provider
profile, independently of vLLM or another server's own settings. Changes apply to new requests
and existing queues within the five-second `PlatformLlmJobService` maintenance pass, without an
AgentHost restart. Lowering the limit lets active requests finish before admitting more.
The persisted profile setting replaces `CSweet:Llm:Queue:MaximumConcurrentRequests` and its
environment-variable equivalent. See [inference waiting](llm-queue-and-startup-recovery.md).

### Agent token budgets

`AgentTokenBudget` supplies editable `maxContextWindowTokens` (256,000 by default) and
`maxOutputTokens` (128,000 by default) through `AgentConfigurationRules` and
`AgentInstallationConfigurationService`. LLM agents expose these fields even when an older
package omitted them. Definition defaults and employee overrides use the same configuration
and restore paths as other agent settings. Existing saved values are preserved.

`PlatformLlmCapabilityHandler.StreamAsync` resolves the authenticated installation's effective
output budget for every provider call. Per-call `ChatOptions.MaxOutputTokens`, including legacy
hardcoded workflow budgets, and provider-profile output defaults do not override it. The former
`CSweet:Llm:Queue:DefaultMaximumOutputTokens` fallback is removed. This platform correction applies
to existing agent packages after rebuilding/restarting AgentHost; repackaging each agent is not
required. Configuration updates are read on subsequent inference calls.

Both values must be positive whole numbers, with output below total context. Invalid saved values
produce an actionable configuration error rather than being silently clamped. The context setting
remains the agent harness's planning/compaction budget; it does not enlarge the provider model's
actual context capacity. Provider rejection of an unsupported budget remains a failure, without
an automatic retry at a smaller budget. Provider-profile defaults still apply to ordinary
non-agent provider clients.

### Request size limits

`CSweet.AgentHost` reads `CSweet:Llm:Queue:MaximumRequestBytes` and
`CSweet:Llm:Queue:MaximumMessageCharacters`. Both default to **0**, meaning no
application-level inference byte or text-size limit. Set a positive integer to enforce a cap;
negative values fail startup validation. For example:

```json
{
  "CSweet": {
    "Llm": {
      "Queue": {
        "MaximumRequestBytes": 0,
        "MaximumMessageCharacters": 0
      }
    }
  }
}
```

Environment variable equivalents are `CSweet__Llm__Queue__MaximumRequestBytes` and
`CSweet__Llm__Queue__MaximumMessageCharacters`. Restart AgentHost after changing them.
`PlatformLlmJobService.StartAsync` and `PlatformLlmCapabilityHandler.StreamAsync` both
honor the optional byte cap, including the provider request after ticket context is added.
Configured validation failures return an MCP invalid-parameters response instead of HTTP 500.

Zero disables these inference checks; it does not change the selected model's context capacity,
message/tool-count controls, concurrency controls, or the separate MCP/Office transport bounds.
The MCP envelope still defaults to 16 MiB, with Office framing imposing its own bound.
Requests larger than those transport bounds require a separate transport configuration/design change.

## Verifying Your Setup

### Health Endpoints

After starting the API project, verify it's running:

```powershell
# Check custom health endpoint
curl http://localhost:<port>/api/health

# Expected response:
# {"status":"ok","service":"CSweet.Api"}

# Check built-in health check (from ServiceDefaults)
curl http://localhost:<port>/health
```

### Blazor App

After starting the App project, open the URL shown in the console (typically `https://localhost:<port>`) and verify:
- C-Sweet branding is visible
- Environment label shows "Development" or "Production"
- API connectivity badge shows Connected/Disconnected based on `/api/health` availability

## VS Code Launch Configuration

For a complete debug experience, create `.vscode/launch.json`:

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": ".NET Core Attach (AppHost)",
            "type": "coreclr",
            "request": "attach",
            "processId": "${command:pickRemoteProcess}"
        },
        {
            "name": ".NET Core Launch (CSweet.Api)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/CSweet.Api/bin/Debug/net10.0/CSweet.Api.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/CSweet.Api",
            "console": "internalConsole"
        },
        {
            "name": ".NET Core Launch (CSweet.App)",
            "type": "coreclr",
            "request": "launch",
            "preLaunchTask": "build",
            "program": "${workspaceFolder}/src/CSweet.App/bin/Debug/net10.0/CSweet.App.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/CSweet.App",
            "console": "internalConsole"
        }
    ]
}
```

## Troubleshooting

### Pages freeze after an unread-message refresh times out

If the browser console reports `TaskCanceledException: net_http_request_timedout, 100`,
`CommunicationUnreadState.OnBusinessChanged`, and `[MONO] Process terminated`, an HTTP
timeout escaped the unread refresh's background event handler and terminated the Blazor
runtime. `CommunicationUnreadState.ReloadAsync` must handle `OperationCanceledException`
even when the caller's cancellation token is not cancelled: `HttpClient.Timeout` uses its
own cancellation. The refresh retains the previous counts and releases its lock so a later
business change or realtime event can retry. `NotificationSoundTests.UnreadState_HttpTimeoutRetainsCountsAndAllowsTheNextRefresh`
exercises a real HTTP timeout and successful recovery. Rebuild the frontend and reload the
browser to recover a terminated runtime. Investigate API connectivity separately if requests
continue to time out.

### Employees remains on "Loading employees" while API health succeeds

`OrganizationUserService` depends on personal work, and collaboration depends on hiring and
employee services. Eagerly injecting `IAgentCoordinationService` into `WorkItemMutationEngine`
closes that construction cycle through the `IPersonalTodoService` factory in
`DependencyInjection.AddCSweetInfrastructure`. The factory hides the cycle from normal
constructor validation and requests can stall before executing the employee query.

Register the mutation engine with a deferred collaboration factory, resolving collaboration
only during recovery operations. `InfrastructureDependencyInjectionTests.EmployeeAndCollaborationServicesResolveWithoutAConstructionCycle`
checks both service resolution orders; `AgentCoordinationServiceTests.ManagerTicketRequeueResumesSpecialistAndPreservesVisibleDiagnosis`
verifies deferred resolution still resumes the specialist. Rebuild and restart affected
service hosts to load the corrected registration; browser reloads alone cannot fix this cycle.

### Always-on agent startup retries and server restart recovery

`AgentRuntimeManager.HandleAlwaysOnTermination` schedules transient MCP/transport startup
failures using persisted `AgentSchedule.NextTickAt`. The delay doubles after each failure,
starting at 30 seconds and remaining at 30 minutes once the maximum is reached. These retries
continue beyond three failures. `EnsureAlwaysOnRuntimesAsync` respects the due time, so the
five-second schedule reconciliation loop does not bypass backoff. A successful MCP session
resets the failure count through `AgentRuntimeSignalService.RecordMcpSessionEstablishedAsync`.

Configure `CSweet:AgentRuntime:StartupRetryInitialDelaySeconds` and
`CSweet:AgentRuntime:StartupRetryMaximumDelaySeconds` in WorkerHost configuration (or the
corresponding environment variables with `__` separators). Values are bounded to one second
through one day, with the maximum at least the initial delay.

`AgentRuntimeStartupCleanupWorker.StartAsync` calls
`AgentRuntimeStartupCleanupService.RecoverTransientStartupFailuresAsync` once per server
start. Enabled always-on installations with a transient failed startup and no active runtime
become due immediately, including legacy failures previously suppressed after three attempts.
The original failure and failure count remain visible. The schedule update and runtime
diagnostic event are saved together; the existing schedule worker discovers due state even
without a connected agent. Broker availability and runtime eligibility still govern launch;
another startup failure returns to backoff. Disabled agents, manual cancellations, and fatal
package failures are not reopened by this recovery. An explicit **Retry startup** or corrected
configuration remains the recovery path for those package failures.

`AgentRuntimeManagerStartupRecoveryTests` covers increasing and capped delays, scheduler due
time enforcement, restart recovery of legacy timeout diagnostics, duplicate discovery, and
disabled/fatal cases. No token budget is changed by startup recovery.

### Hired agent is offline and has not sent an introduction

`OrganizationUserService.CreateAsync` persists the employee and onboarding event before
requesting an always-on runtime. `AgentOnboardingEventDispatcher` keeps the event durable until
the hired installation acknowledges it. A successful `AgentHireOperation` therefore does not
prove the agent started or sent its introduction.

Check the installation's latest `AgentRuntimeInstance` status, reason, and log excerpt, its
`AgentSchedule.AutomaticStartSuppressedAt`, and the matching `AgentOnboardingEventOutboxItem`
status. An `AgentRuntimeWorker.ValidateIdentity` failure means the compiled agent's `AgentId`
or `Version` differs from its packaged `csweet-plugin.json`; the agent exits before the MCP
session and before processing onboarding. `McpAgentSessionService.EstablishAsync` also requires
the exact approved package identity. Correct and publish a new package version, deploy it to
the existing definition/installation, then use **Retry startup** if automatic startup remains
suppressed. Do not rehire or clear the pending onboarding event: its delivery key includes the
package revision, so the updated package can receive the original event and send its intro.

### A Chat Turn Failed With a Generic Message

Communications shows "The agent couldn't complete that request. Please try again." when a durable chat
turn fails. The cause is persisted on the turn row, its trace events, and (for agent-side failures)
the agent work item. Follow [Chat turn diagnostics](./chat-turn-diagnostics.md) before retrying.

### Agent collaboration stopped after a model response

A dead-lettered coordination work item moves its `AgentCoordinationSession` to `Failed`.
`AgentWorkInbox.FailCoordinationForWorkAsync` also creates an important notification for
human managers in the source chat, or the business owner when no manager is a participant.
The notification opens Communications, where the failed collaboration card shows a safe
failure explanation and **Retry collaboration**. The original transcript stays intact.
After updating the agent package that caused the failure, use that action to resume the
failed speaker. Repeated attempts before updating the package repeat the same failure.

For Producer pitch refinement, inspect the `AgentWorkItem.LastError`, the matching
`AgentWorkAttempts`, and `AgentRuntimeInstance.LogExcerpt`. A
`agent.payload_invalid` / `JsonException` with extra content after one JSON value
was addressed in `PitchProtocol.ParseProducerReview` (Producer 2.8.4). A Creative
Director attention review failing `work.personal-todo.add.v1` because source
conversation and message IDs were not supplied together was addressed in
`VideoGameCreativeDirectorAgent.EnsurePortfolioAgendaAsync` (Creative Director 1.11.5).

### Docker Engine Is Unavailable

Run:

```powershell
docker info
```

The command must succeed before AppHost starts. If it does not:

1. Open Docker Desktop.
2. Ensure Docker Desktop is using Linux containers.
3. Wait until Docker Desktop reports that the engine is running.
4. Run `Start-CSweet.cmd` or start AppHost again.

If `docker` is not recognized, install Docker Desktop using the link in the root README, then reopen the terminal so its PATH is refreshed.

### Aspire Postgres Authentication

Aspire uses Postgres credentials from `src/CSweet.AppHost/appsettings.Development.json`:

```text
CSweet:Postgres:UserName
CSweet:Postgres:Password
CSweet:Postgres:Database
```

If Postgres logs `password authentication failed` after credential changes, the existing Docker volume was likely initialized with older credentials. Delete the Aspire development volume once:

```powershell
docker volume rm csweet-aspire-postgres
```

After that, rerun AppHost. The volume should not need to be deleted again unless the configured credentials change.

### Port Already in Use
Aspire assigns random ports by default. If you need fixed ports, update the AppHost `Program.cs` with `.WithExternalHttpPorts()`.

### Aspire Dashboard Not Opening
The dashboard URL is printed to the console. Look for a line like:
```
Now listening on: https://localhost:15887
```

### "Unable to Connect" in Blazor App
When running `CSweet.App` standalone (without AppHost), the app tries to call `/api/health` relative to its own base URI. Since no API is running there, it shows "Disconnected". This is expected — run via AppHost for full connectivity.

## Local Office update source selection

With the Windows development launcher configured for the current host, ExecutionFleet offers an
Office update even when only the local source fallback is available. Every setup, reconnect, repair,
and upgrade asks the launcher for the latest compatible stable prebuilt bundle first. The configured
sibling checkout is used only when release discovery is unavailable. The LocalOfficeUpgrade workflow
still preserves drain, zero-active-work, UAC, certification, and identity-preserving installation checks.

ExecutionFleetService.LaunchLocalSetupSessionAsync claims the launch in the database before starting
an elevated process. Repeated requests for that session return its current state; a failed process
start releases the launch claim. An interrupted server cannot launch the same handoff again merely
because the browser retries.

Initialize-CSweetWindowsIsolationTest.ps1 uses CSweet.DevelopmentBuild.ps1 to serialize source builds
across PowerShell and the guided launcher. Verified prebuilt bundles bypass that source-build mutex;
their unique staging roots proceed directly to target-host certification, local signing, packaging,
and installation. Source fallback still waits for live developer-bootstrap progress owners from older
scripts as well as the shared mutex and never stops another build. Only completed preparations return
PayloadResultPath; Start-CSweetDevelopmentOfficeSetup.ps1 consumes that exact result instead of
selecting the newest directory.

ExecutionFleetService.ReadWindowsSetupProgress checks the owner PID for running progress records.
After a 30-second grace period, a record whose owner has exited is surfaced as
`office_setup_interrupted`, allowing onboarding to start a new session instead of polling stale
progress indefinitely. A matching live owner remains authoritative even when a long download or
certification phase has not recently rewritten the file.

Verification: scripts/tests/Test-DevelopmentBuildCoordination.ps1 in CSweet.Office covers competing
processes, a legacy progress owner, completed records, and waiting peers. ExecutionFleetServiceTests
covers repeated launch requests preserving the original approval timestamp and session.

## Blocked personal epic fails to requeue

If moving a personal epic from Blocked to To Do reports
"Collection was modified; enumeration operation may not execute", inspect
`AuditPayloadSanitizer.Redact`. Redacting string values replaces children in their parent
JSON array; traversal must snapshot the array before those replacements. A serialized
`WorkTask.PlanningSpecificationJson` contains acceptance-criteria arrays and triggers the
same path during automatic recovery.

`WorkItemMutationEngine.RequeueAsync` and `RecoverAgentUpdatedBlockedWorkAsync` save the
ticket transition, reopened plan children, and wake records together. A failure during
audit capture prevents that save. Updating the agent package alone cannot fix server-side
audit capture. Rebuild/restart C-Sweet (API and AgentHost) after applying the server fix.
`PersonalTodoReconciliationWorker` runs at startup and every 30 seconds; it retries eligible
development blockers whose active installation was updated after the failure.

Regression coverage: `AuditPayloadArrayTests` exercises nested arrays and serialized
planning specifications while verifying redaction; `PersonalTodoServiceTests` covers
manual epic requeue and automatic agent-update recovery in fresh tracking contexts.

## Development blockers lack actionable detail

`SoftwareDeveloperAgent.DevelopmentBlockerMessage` in the Software Developer repository
(version 1.8.5+) includes the failed step, a bounded diagnostic, and a recovery action.
Platform failures include the capability and failure code; exhausted task validation includes
the failed command. An unknown compute outcome requires checking the existing operation before
replay, and is not automatically described as a Docker build failure.

`AgentTicketFeedback.ReportedReason` preserves the multiline owner-facing blocker when creating
an `agent.failure` comment. `EmployeePersonalBoard` and agent comments in `WorkItemComments`
render those reports through `ChatMarkdown`. Rebuild/restart C-Sweet and update the Software
Developer installation to use both halves of this fix. Historical comments are not rewritten.

Regression coverage: `DeploymentDiagnosticTests`, the exhausted-budget case in
`ComputeDeploymentRecoveryTests.CompleteBacklogPrecedesCodingAndRestartAdvancesOnlyOneTaskWithFreshEvidence`,
and `AgentTicketFeedbackTests.ReportedBlockerPreservesEvidenceAndRecoveryStepsInPersistedComment`.

## Local compute fails for a second business

`ComputeLocalInstallation` and `scripts/Get-ComputeLocalInstallation.ps1` select the
installation by business. An existing first business retains `CSweet.Compute.HyperV`
and `%ProgramData%\CSweet\Compute`. Additional businesses use
`CSweet.Compute.HyperV.<organization-id-N>` and a sibling
`%ProgramData%\CSweet\ComputeBusinesses\<organization-id-N>` directory. The sibling
location prevents legacy installation upgrades from traversing another business's files.
Each business has its own node, signing certificate, catalog, journal, workload directory,
service recovery policy, and per-provider capacity. Host capacity must accommodate the
combined allocations; these limits are not a shared machine-wide resource budget.

Previously `Install-ComputeLocalProvider.ps1` reused the first service's node during
second-business enrollment. `ComputeLocalSetupEndpoints` correctly rejected that node
with a conflict, surfaced as `local_setup_failed`. Preserve this ownership check.
`LocalComputeInstaller.ReenrollAsync` now restores only the selected business and never
reassigns another business's enrollment or retires its workloads. Recreated businesses
with new IDs receive new installations; old installations require explicit retirement.

Use **Repair Linux preparation** on the affected business's Compute page after updating
the source checkout. The development installer builds the current provider and registers
only that business's service; the first business does not need a restart. Approve the
normal Windows administrator prompt. Setup completion creates the existing scoped grants
and durable `com.csweet.compute.available.v1` wake event so waiting agents can resume.

Regression coverage: `ComputeLocalInstallationTests`, `ComputeWindowsServiceTests`,
`ComputeRuntimeVerifierTests`, and `tests/ComputeLocalInstallation.Tests.ps1` cover
installation selection, retry stability, independent journals, signed cross-business
rejection, service lifecycle, and the PowerShell service arguments.

## Linux preparation fails at compute installation validation

If `Install-ComputeLocalProvider.ps1` reports `Compute installation validation failed`
after the provider prints command-line usage, inspect
`Get-ComputeLocalInstallation.ValidationArguments`. The default installation needs a
single-element array containing `validate-service`; PowerShell splatting a scalar
string passes its individual characters instead. The helper now preserves an array
for both default and business-scoped installations, and the installer normalizes
the arguments before both validation calls.

Update the source checkout, then use **Repair Linux preparation** on the affected
business's Compute page. The installer reads the updated scripts on the next attempt.
An already verified Linux image can be reused. No database reset or image deletion
is needed. `tests/ComputeLocalInstallation.Tests.ps1` covers actual argument splatting
for fresh, retained default and business-scoped installations.

## DeepSeek rejects a tool continuation with missing reasoning content

An HTTP 400 containing `reasoning_content` and "must be passed back" is a request
compatibility failure, not a provider outage. `Microsoft.Extensions.AI.OpenAI` 10.10.0
reads the field into `TextReasoningContent` but omits it when rebuilding assistant history.
DeepSeek requires that field on subsequent tool-enabled requests, including earlier final
answers. See the [DeepSeek thinking-mode contract](https://api-docs.deepseek.com/guides/thinking_mode/).

`OpenAiCompatibleLlmProviderFactory.AdaptChatClient` uses `ReasoningContentChatClient` to
restore the exact supplied reasoning text on the serialized assistant messages. The policy
is local to each request, leaves native reasoning fields intact, and preserves the SDK's
serialization of tools, media, roles, and options. It does not invent missing reasoning or
disable thinking. The broker and `PlatformChatClient` already carry `TextReasoningContent`.
`LlmProviderFailureMessage.From` now names this compatibility failure without exposing raw
provider bodies or credentials.

Rebuild/restart AgentHost after applying the fix. Requeue the affected personal plan's root
epic through the normal board action to reopen its blocked running story/task and emit the
durable wake. No provider credential change or agent package update is needed.

Regression coverage: `ReasoningContentChatClientTests` inspects real SDK HTTP payloads for
streaming and non-streaming tool continuations, final-answer reasoning, empty reasoning,
unchanged caller history, and concurrent conversations; `LlmProviderFailureMessageTests`
checks the safe recovery message.

## Completed development epic has an unclear review handoff

`SoftwareDeveloperAgent.ReviewDeliveryMessage` (agent 1.9.2+) puts the visible review URL
first and separates the handoff from the retained coding report. `CompletedAsync` requires
message delivery before the final deployment task is marked complete; restart retries retain
the same message key. `EmployeePersonalBoard` renders result summaries through `ChatMarkdown`
so review links are clickable from the completed ticket as well as Communications.

`NodeDeploymentValidationScript` executes the source snapshot's package.json test script in
the cached Node runtime before building the application image. Tests fail closed when the
script is missing or fails. A successful HTTP check alone proves serving, not product
completeness: review the actual entry point and feature wiring if a completed epic still
serves a scaffold. Historical completed deployments are not automatically rerun by this update.

Verification: `ReviewDeliveryTests` covers delivery ordering, interrupted delivery/reporting,
expired or unverified URLs, and retained technical evidence; deployment recovery tests cover
Node-suite failures entering bounded repair without issuing a review link.

## Office onboarding uses a tagged development bundle

The Windows AppHost launcher no longer needs a sibling Office checkout. Setup, reconnect, repair,
and upgrade first check stable GitHub Office releases for `office-bootstrap.json`, verify the selected
bundle, then certify and development-sign the guest on this machine. No production signing key is
needed. `CSweet.OfficeRelease.ps1` reports a download/integrity failure; it only uses configured source
when release discovery is unavailable. Source fallback still needs its SDK, image tooling, and cached
dependencies.

The launcher stages full bundles under a short unique `%ProgramData%\CSweet\Setup\b-<16 hex digits>`
path. Longer extraction roots can exceed Hyper-V path limits when it appends certification VM files.

See [release-first onboarding](features/office-assisted-installation.md) for the code map and
receipt-bound enrollment refresh. Release `v0.6.1` passed live download validation and real Hyper-V runtime/builder certification
from the short staging path, including development signing and payload creation without installation.
For the fresh-install test, remove the configured
Office source path (or start AppHost without the sibling checkout), create a fresh setup session,
and verify download, Hyper-V certification, enrollment, approval/heartbeat, and readiness.
A fresh database does not remove an installed Office: use the existing explicit reconnect/remove
recovery flow when setup detects retained services or identity.

## Producer created tickets but its draft sprint never starts

Inspect the Producer runtime's retained failure before treating a generic connection message as
an infrastructure outage. `SpecialistAgent.PublishCanonicalBacklogAsync` in Producer 2.9.0
combined the 64-character planning fingerprint, 64-character artifact digest, and proposal key
in a `producer-hierarchy` mutation key. Normal proposal keys such as `NC-T-CONTENT-ENG`
exceed the broker's 160-character limit, so updating an already-published backlog fails with
`JSON Schema validation failed: $.idempotencyKey is too long`. The runtime classified this
HTTP request failure as `runtime.transport`; repeated failures blocked the planning commitment.
The sprint remains `Planned`, although ticket stage assignments may already be populated.

Producer 2.9.1 uses `BoundedMutationKey` for ticket creation and planning revision. It retains
valid legacy keys and hashes the complete oversized identity, preserving retry stability and
separating different proposals and revisions. `BacklogRevisionRecoveryTests` exercises a
previously published ticket with full digests, verifies the request fits the broker limit,
and confirms a second reconciliation keeps the same ticket without another revision.

Deploy the corrected Producer package to the existing installation, then requeue its blocked
planning commitment through the personal board. The Producer must still collect role estimates,
obtain QA readiness, pass sprint preflight, and start orchestration. Do not manually mark the
sprint active or bypass those checks. Package test success is not project completion evidence.

## Technical planning repeats already-decided questions

Producer 2.9.1 may finish backlog publication recovery yet remain in planning because Technical
Director 2.11.0 omitted the current coordination request from its model messages. Producer
`EnsurePlanningSessionAsync` correctly included recorded manager decisions in `InitialMessage`,
but Technical Director `HandleCoordinationTurnAsync` supplied only repository facts, canonical
identities, roles, skills, and accepted documents to inference. The model reopened delegated
engine/performance/tuning questions and produced a new proposal instead of consuming the answers.

Technical Director 2.11.1 includes `PlanningCoordinationMessage` in every generation attempt,
including bounded correction and compact retries. Only the authenticated counterpart's request
matching the exact planning cycle is included. Delegated technical investigations are planned as
research tasks; genuine external-authority questions retain their escalation path. Producer 2.9.2
uses the `directions-v2` recovery key to avoid replaying the completed legacy proposal. Deploy the
Technical Director update before the Producer update so that this recovery runs the corrected
planner. Both retain the existing project, accepted brief, ticket identities, and approvals.

Regression coverage: `ProductionPlanningTests.Current_manager_direction_survives_every_generation_attempt`
and `PlanningDirectionRecoveryTests.Direction_recovery_is_distinct_from_legacy_but_stable_across_retries`.
Live sprint execution and outcome acceptance must still be verified after rollout.

## Corrected planning fails assignment validation or leaves stale criteria

Producer `PublishCanonicalBacklogAsync` through 2.9.2 updated a ticket's delegation recommendation
but submitted its old stage-assignment requirements in the same revision. A corrected proposal
that changed required skills could therefore fail with `Assignment must satisfy the ticket's
explicit delegation requirements` before `BindAvailableWorkAsync` could repair the assignment.
The prior reconciliation also retained old descriptions, acceptance criteria and dependencies.

Producer 2.9.3 refreshes assignments against the corrected specification within the same mutation.
It retains eligible owners, clears ineligible assignments, and applies current title, description,
requirements, criteria, constraints, dependencies and accepted-package evidence. Completed and
in-progress work remains immutable. A `planning-v2` mutation identity recovers older partial
reconciliations; matching content and assignment evidence avoid repeated revisions.
`BacklogRevisionRecoveryTests` covers changed skills, retained and ineligible owners, updated
specifications, completed dependencies and retry stability against the published SDK.

## A general Software Developer rejects the Producer's estimation session

`DevelopmentCoordinationService.HandleAsync` through Software Developer 1.11.11 accepted only
project-intake assistance and work-item architecture support. A `game-engineer` assignment to
that core-role agent was eligible, but the Producer's board-scoped
`video-game.production.role-estimate-request.v1` request was rejected before estimation.

Software Developer 1.12.0 adds `ProductionEstimation.TryHandle` for the existing production wire
protocol. It validates counterpart authorship, project/board context, request fingerprint,
planning revision, developer-role ownership and per-item evidence. It returns an exact-batch
estimate proposal using the specialist kit's initial sizing policy with explicit conditional
capacity assumptions. It performs no ticket mutation, QA approval or sprint activation and adds
no grants. `ProductionEstimationTests` covers both developer role labels, malformed and
uncorrelated requests, stable replay, and cancellation. Existing architecture-support tests
continue to cover the prior coordination path.

## Producer rejects estimation or preflight priority

Producer 2.9.3 could successfully reconcile the technical proposal, then fail creating its next
personal commitment with `JSON Schema validation failed: $.priority is not an allowed value`.
`ReconcilePlanningAsync`, `ReconcileEstimatesAndQaReadinessAsync`, and both attention-recovery
branches used the unsupported literal `Urgent`. Producer 2.9.4 uses `WorkPriorities.Critical`
at all four call sites, matching `McpToolCatalog`'s personal-todo schema. The planning state was
already saved before the rejection, so the next attention review can reuse it to request role
estimates without repeating Technical Director inference.

## Producer estimates rejected before provenance reaches the handler

`McpToolCatalog.InputSchemaFor` omitted `EstimateWorkItemRequest.Provenance` from the strict
`work.item.estimate` schema even though `WorkManagementCapabilityHandler.EstimateItemAsync`
already persists that typed evidence. The Producer completed role-estimation collaborations but
both its original and attention-recovery commitments failed with
`JSON Schema validation failed: $.provenance is not allowed`.

The broker schema now accepts nullable typed provenance with bounded source digest, source and
session UUIDs, positive coordination revision, and confidence between zero and one. It still
rejects unknown nested fields. `McpSprintPlanningSchemaTests` validates actual serialized wire
requests, legacy estimates without provenance, malformed evidence, and the subsequent comment,
move, scope, capacity, preflight and start requests. Restart AgentHost after rebuilding this
platform change; no agent-package update or grant change is needed. Saved estimates and
coordination artifacts remain available to the Producer's next recovery review.

## Sprint preflight rejects generalists or unfinalized specialist work

`WorkOrchestrationService.ValidateAssignmentEvidenceAsync` formerly read optional
`rolePolicy.specializationKeys` with `GetProperty`. Valid generalist manifests that omitted the
array failed with a generic invalid-policy message. `ValidateManifestAssignmentPolicy` now
interprets absence as an empty set while rejecting malformed present values and retaining all
core-role, required-skill and selection-evidence checks. `WorkAssignmentManifestPolicyTests`
covers optional metadata, same-core assignment, required skills, wrong roles and invalid data.

Technical Director `FinalizeEngineeringTicketsAsync` formerly finalized only engineering tickets.
Research, audio and QA tickets remained planning-only, preventing the entire sprint from starting.
Technical Director 2.11.2 uses `FinalizeExecutableTicketsAsync` and `DeliveryFinalizationRequest`
for staffed executable work on unscheduled/planned scope. Engineering retains its technical,
QA and governed-merge stages; document specialists retain Producer review. The finalizer also
refreshes stale delivery instructions to match later approved planning, retaining existing owners
and additional delivery metadata. Matching delivery specifications, terminal/running tickets and
active/closed sprint membership remain untouched. `EngineeringDeliveryTests` covers scope and
identity preservation, specialist routes, revised scope and replay stability.

## Sprint starts but its first execution never advances

`WorkOrchestrator.DispatchAsync` initializes a canonical ticket's assignment revision before
building the assignment envelope. This path does not call the legacy software-development
assigner. Review and merge stages retain the initialized revision. Workflow traversal counts
start at zero; attempt numbers and assignment revisions start at one. Technical Director 2.11.3,
Audio Designer 2.3.3 and QA 2.4.4 accept the initial zero traversal while rejecting negative
values. Audio Designer also uses web JSON options for canonical execution inputs.

Dispatch now commits the inbox message, explicitly-added execution attempt, stage state and
attempt grants in one transaction before requesting a runtime wake. Previously an attempt-save
failure could leave an inbox message behind; subsequent reconciliation generated a different
attempt UUID under the same idempotency key and failed indefinitely. `RecoverDetachedAttemptAsync`
reconnects matching historical inbox work to its original attempt after validating the complete
execution identity. It does not resend work or discard failure history. Normal retry policy
then consumes its actual result. `WorkDispatchRecoveryTests` verifies this recovery, revision
preservation, mismatched identity rejection, and relational commit/rollback with an injected
failure after the inbox save. Restart the API to load the orchestration worker changes.

`WorkManagementCapabilityHandler.FinalizeItemDeliveryAsync` also persists a project-bound
`WorkItemChangedV1` wake event with the finalized ticket. Agents re-read current state after
this hint; replaying finalization does not emit another event. This closes the notification gap
that otherwise leaves sprint preflight waiting for the next scheduled attention review.
Restart AgentHost to load this broker change.

### Specialist document generation and current execution blockers

`VideoGameSpecialistAgentBase.ExecuteCapabilityCoreAsync` in the Technical Director package validates role-specific sections after inference. `SpecialistDocumentPrompt.Create` must include the same section names in the generation prompt; otherwise a substantive response can fail on a requirement it was never given. Technical Director 2.11.4 also requires the response to distinguish design proposals from measured execution and committed repository evidence.

`WorkOrchestrator.ReconcileAttemptAsync` updates `WorkStageExecution.LastError` from the current blocked/failed outcome and clears it for completed outcomes. Its blocked branch also updates the canonical ticket's `BlockReason`; an earlier infrastructure error must not obscure a later specialist diagnosis. `WorkDispatchRecoveryTests.Blocked_result_replaces_previous_attempt_error_on_stage_and_ticket` covers this sequence.

`WorkManagementCapabilityHandler.ReadLatestOutcome` exposes the exact validated latest blocked or failed agent result, including its diagnosis, to authorized orchestration readers. It checks stage/attempt identity, disposition, outcome code, and completed attempt status. Failed trusted-platform actions remain excluded from completion evidence. Producer 2.9.5 uses these reads and its existing `work.orchestration.retry` authority to retry missing-section failures once per stage and error digest, retaining the observed assignment revision and host retry budget.

### Technical planning versus implementation recovery

`SpecialistAgent.RolePrompt` and `ProductionPlanning.cs` in the Technical Director agent keep the role responsible for plans and reviews; engineers own code, prototypes, package locks and commits, and QA owns independent validation. `EngineeringPlanningRequest` and `DeliveryFinalizationRequest` also retain review and governed-merge requirements when an already-finalized ticket is reassigned to engineering.

The Producer's `DeliveryAcceptance.cs` assesses mixed-role criteria in blocked Technical Director assignments and submitted documents. `RoleReplanning.cs` persists the original scope and creates a durable personal planning commitment. `ValidateRoleRepairCoverage` rejects lost requirements/criteria/constraints, unrelated scope changes, missing identities, invalid dependencies and execution criteria left with the Technical Director. `ReconcilePlanningAsync` requests a corrected technical proposal before `PrepareRoleRepairSprintAsync` cancels the incompatible execution and carries unfinished work to a planned replacement. Cancellation uses the Producer's separately declared, host-authorized `work.orchestration.cancel` capability; no database repair or role impersonation is required. The old sprint and attempts remain auditable, and fresh estimates/readiness/preflight still gate execution.

`EnsurePlanningSessionAsync` keeps the objective within the host's 4,096-character field and carries bounded detailed evidence in the initial message. Full original scope comes from the canonical board, including dependency identities. An invalid corrected proposal remains blocked before cancellation; an infrastructure failure alone is not treated as a role conflict.

`WorkOrchestrator.DependencyDocumentsAsync` supplies direct dependents with the exact document revision produced by the dependency's latest completed attempt and accepted by the board manager in the same traversal. It verifies organization, board, origin ticket, project/team, creator, producing installation and content hash; newer drafts never replace delivered revisions. This bounded assignment handoff adds no artifact browsing or editing grants. `AssignedDevelopmentService.ReadDependencyPlans` validates the declared dependency and digest before adding the content to the coding prompt as untrusted project evidence. `SuccessfulOutcomeCode` selects `code-published` when the immutable stage policy supports it, retaining `completed` for legacy code workflows.

### Agent host stops after a database timeout

`ManagementReviewScheduler.ExecuteAsync` logs failed dispatch passes and waits for its next scheduled tick with a fresh scope. A database timeout must not escape this background service and stop the agent host. `ManagementReviewSchedulerRecoveryTests` covers a timeout, dependency cancellation, scheduled retry, and clean host shutdown. If all agents stop while the API stays healthy, inspect the AgentHost resource and Windows Application `.NET Runtime` events for the originating background-service failure.

### Completed agent result never reaches the next stage

If an inbox work item is completed but its execution attempt stays pending/running, inspect API logs for `WorkOrchestrator.ReconcileExecutionAsync` throwing "Collection was modified". Accepting a successful result appends the successor stage to the same item. The reconciliation pass must materialize its active-attempt stages before processing results, so adding a review stage does not invalidate enumeration and discard unsaved progress. `WorkDispatchRecoveryTests.Completed_worker_result_advances_once_and_survives_a_fresh_reconciliation` verifies persisted completion, a single review stage, and replay without redispatch. Restart the rebuilt API; its next pulse consumes the already-saved result without another model run or resetting attempts.
`ArtifactCapabilityHandler.AssignedReviewRevisionAsync` permits the current board-manager approval assignment to read only the exact document revision delivered by its completed predecessor stage. It rechecks active employee identity, current board manager, active/latest execution, review identity/traversal, policy transition, source ticket/project/team, producing author/installation and revision hash. The response excludes other revisions; list, revise and document-decision actions still require their existing grants. Closing/reassigning the review or cancelling execution ends this input access without persistent permission changes. `DeliveryReviewDocumentAccessTests` covers these boundaries. Restart AgentHost to load this reviewer-input path when Producer acceptance fails at `platform.artifact.read.v1` despite a valid submitted worker result.
### Existing game team fails the explicit project-participant gate

Team membership and `ProjectParticipants` are separate. Older governed projects can have a fully hired delivery team with no explicit participant records; `ProjectWorkPolicy.RequireAsync` still rejects development until an authorized human selects participants in **Manage members**. `ProjectSetupService.UpdateMembersAsync` and `ProjectSetup.razor` retain the project's existing assigned manager even when that profile uses a Producer instead of a software product manager. This retention does not authorize arbitrary agent managers for new projects or replacements. Saving the selected existing team uses the normal membership/grant/event transaction and preserves reporting lines. `ProjectSetupTests.Existing_profile_manager_can_be_retained_when_explicit_participants_are_added` verifies this compatibility path and the unchanged manager-selection boundary.

Reconciliation also commits completed stages before dispatching dependent work. Dependency-document reads use persisted state; without this checkpoint they see the previous pending status, throw, and discard terminal completion again. `WorkDispatchRecoveryTests.Terminal_completion_is_durable_even_when_a_dependent_dispatch_fails` verifies that a downstream dispatch failure cannot undo completion. Inbox work, attempt identity and execution grants remain atomic inside each separate dispatch transaction.

`ProjectWorkPolicy.RequireStageAssignmentAsync` authorizes `work.execution.run.v1` against the exact current stage, employee/installation, item, sprint, policy and traversal. Enqueue requires the next pending attempt; claim requires that exact persisted active attempt. This is separate from legacy direct-development ticket ownership, because canonical stage staffing can assign different workers and reviewers without filling the legacy owner fields. Both paths still require explicit project participation, team membership and board read access; archived or foreign tickets and completed/cancelled workflows fail closed. `ProjectStageAssignmentTests` covers enqueue/claim and invalid identities. Restart both API and AgentHost after this shared inbox-policy change.
`ProjectCapabilityPolicy.ValidateAsync` applies the same project prerequisite to Git workspace requests from a currently running canonical stage. It requires the exact active stage owner, current traversal and a persisted active attempt, then enforces project participation. `GitWorkspaceCapabilityHandler` still independently checks assignment revision, repository/team policy and the permitted attempt-scoped Git actions. This avoids rejecting correctly assigned engineering/QA work solely because legacy ticket owner fields are empty.
`SpecialistAgent.TryReadRetryDirection` and `RetryDirectedTicketAsync` in Producer 2.10.5 let its authenticated reporting manager explicitly request recovery with `Retry ticket IDENTIFIER: reason`. Discovery is bounded to current owned boards and active executions; the exact current blocked agent stage, assignment revision and attempt budget are re-read before using the existing retry capability. A stable turn/stage key prevents duplicate delivery from spending another attempt. The host's assignee/accountable-manager boundary is unchanged. `DirectedDeliveryRecoveryTests` covers manager identity, foreign/archived boards, cancelled/stale/running stages, ambiguous tickets, attempt exhaustion, replay and host denial. This is the recovery path after repairing a platform prerequisite; installed earlier Producers only retry narrowly recognized document-format failures automatically.

`ChatReportingAuthority.IsAncestorAsync` supplies broker-authenticated `senderIsReportingAncestor` context by traversing the current active, nonarchived, same-organization reporting chain with cycle/depth limits. `ChatTurnWorker` also supplies the exact persisted `currentUserMessage`, distinct from memory/history-enriched model prompts. Producer directed recovery uses these fields; sender text or role names cannot establish authority. Eight `ChatReportingAuthorityTests` cover direct/indirect managers, unrelated/self/foreign identities, inactive/archived records and cycles. Restart the API together with installing Producer 2.10.5 when the Producer reports through an agent manager.

`CanonicalWorkspaceAuthorization.AuthorizeAsync` applies the canonical stage assignment at Core's workspace boundary as well as AgentHost's initial gate. Prepare, inspect, publish, refresh, snapshot transfer and locks require the current running stage/traversal, active attempt, exact ticket revision/repository/team, active employee, current execution's action grant, and explicit project participation. Quality work is pinned to the latest nonsuperseded publication and cannot publish. Legacy ticket-owner and legacy QA-review paths remain available. `AgentWorkspaceBrokerCanonicalTests` exercises successful prepare/inspect/publish and rejects revoked grants, stale identities/traversal, cancelled execution, archived tickets, absent attempts, wrong repository, missing participation and QA publication. Restart the API after rebuilding; a failed pre-inference workspace attempt still requires a bounded retry through its accountable Producer.

The same authorization is used by `WorkspaceVolumeBridge` for snapshot import/export/removal; passing Core preparation alone must not fail the snapshot layer on empty legacy-owner fields. The preparation lease carries the expected publication commit for QA. `CoreWorkspaceBrokerClient.PrepareAsync` and `AgentWorkspaceBrokerEndpoints` preserve credential-safe structured failures and a diagnostic ID, so a pre-inference failure identifies its actual cause. Restart API and AgentHost together.

Producer 2.10.6 accepts `Replan ticket IDENTIFIER: reason` from the authenticated reporting chain after infrastructure repair when the current agent stage has exhausted its attempt budget and no work or approval is active. `SprintRecovery.cs` persists exact scope and a durable personal commitment before cancellation. The existing role-replanning carryover preserves completed tickets and attempt history, then normal estimates and readiness govern the replacement sprint. Lost-response replay is idempotent, including after the replacement has started. This command neither approves delivery nor raises attempt limits.

### Producer waits after a completed coordination session

`AgentCoordinationService.RespondAsync` calls `WorkItemMutationEngine.WakeCoordinationWaitsAsync` when a board collaboration reaches Completed or Blocked. Only unclaimed deferred work owned by the same active initiator/installation, with matching board and any specified project/team/item/session context, returns to Ready. The transition and personal-work outbox record are saved with session completion. This is a wake hint: the agent must re-read the assessment and its normal readiness gates still decide whether to start. Duplicate delivery does not spend revisions or enqueue more work.

`RecoverCoordinationWaitsAsync` performs bounded terminal-session discovery during normal work reconciliation for missed notifications and pre-upgrade completions. The existing deferred review timer remains the fallback. `CoordinationCommitmentWakeTests` covers scope/identity boundaries, claimed/completed/archived work, stale sessions, replay and missed notifications. Restart API and AgentHost after rebuilding this shared implementation.

### Engineering rejects a Ready broker workspace before inference

A broker workspace path is not necessarily mounted inside the isolated agent runtime. `AssignedDevelopmentService.PrepareWorkspaceAsync` in Software Developer 1.12.2 calls the SDK's `MaterializeAsync` and confines the harness to that local snapshot. `UploadAndInspectAsync` sends edited source back before Core inspection; coding checkpoints retain recoverable files without approving or publishing delivery. This uses the existing `git.workspace.sync.v1` authority. `AssignedWorkspaceTests` covers actual archive transfer, retained edits, wrong/unavailable assignments, unsafe archives, and upload denial before inspection. Install the agent update, then have the accountable Producer retry the exact blocked stage within its remaining budget.

### Assigned coding blocked before model startup

`ComputeMcpTools.ReadSchema` accepts `defaults: true` with an optional project `workstreamId`, matching `PlatformComputeClient.GetProjectDefaultsAsync`. `ComputeCapabilityHandler` rejects project scope combined with environment/operation reads; authenticated organization and installation identity still come from the session. `ComputeCapabilityTests` covers the SDK request and invalid selectors.

Software Developer 1.12.3 removes a separate deployment-compute prerequisite from `AssignedDevelopmentService.ExecuteAssignedTicketAsync`: the coding harness runs in the materialized, confined agent runtime workspace and does not consume that VM. Deployment retains its compute-readiness checks. `AssignedCodingRuntimeTests` verifies the assignment reaches model setup after source materialization without deployment compute and does not report completion without model output. Install the release before retrying a stage blocked on the former prerequisite; an agent update alone does not authorize reopening a blocked stage.

`GameQaWorkspace.MaterializeAsync` in Video Game QA 2.4.6 applies the same runtime download boundary to independent QA. It pins the broker-provided commit, downloads without uploading, and checks original file hashes against a fresh authorized snapshot before and after tests, including reconnect cases with retained local edits. Remote inspection alone cannot detect edits in the isolated runtime. The release requests code-only `git.workspace.sync.v1`; publication/merge grants remain absent and snapshot pushes continue to require publication authority at the broker.

### Technical review generates findings but blocks before handoff

`SpecialistAgent.ParseTechnicalDecision` in Technical Director 2.11.7 normalizes only literal escaped whitespace appended outside the decision JSON. A live review returned a valid rejected decision followed by literal `\n`; strict deserialization failed before recording its findings, and the broad exception handler obscured the cause. The parser now preserves the exact decision and findings, retains candidate-SHA and consistency validation, and reports invalid JSON specifically. `TechnicalReviewParsingTests` covers the suffix, changed SHA, inconsistent approval, prose, multiple objects and malformed JSON. Install the release and retry the current technical-review stage through its normal owner; do not mark the candidate approved to unblock it.

`SpecialistAgent.GenerateTechnicalDecisionAsync` in Technical Director 2.11.8 makes one formatting-only retry for malformed decision JSON. A second live response had a complete rejected decision followed by an extra closing brace. The retry retains the original candidate context and must preserve any complete decision prefix, including its summary and findings; wrong SHA, inconsistent decisions, changed decisions during repair, or repeated malformed output remain blocked. `TechnicalReviewRepairTests` covers the bounded retry and rejection preservation. This does not spend another orchestration attempt or bypass technical review, QA, or merge controls.

`AssignedDevelopmentService.ReadReviewFeedback` in Software Developer 1.12.4 carries completed negative `PriorOutcomes` into the implementation prompt with their stage, attempt, exact source commit, summary and findings. It requires matching commit evidence and bounded content, and asks the developer to fix, reproduce or dispute each finding with evidence against the current workspace. `ReviewFeedbackTests` covers technical and QA outcome formats and invalid evidence. Each coding attempt also removes its retained `.csweet/outcome.json` before model execution so an incomplete rework turn cannot reuse an older completion report. Install both releases before retrying the current technical-review stage so a valid rejection can reach engineering with its findings intact.
Live recovery verification (2026-09-27): after Technical Director 2.11.8 and Software Developer 1.12.4 were Active/Current, the Producer's normal bounded retry recorded `technical-review: Completed/rejected` and automatically dispatched traversal 1 of `specialist-execution`. The developer then confirmed the workspace matched the reviewed commit and inspected the reported defects. This verifies review-to-rework coordination, not delivery acceptance: the revised candidate still requires technical review, independent QA and the existing merge gates.
### Owner corrects an active project's acceptance scope

Producer 2.11.1 completes the `Amend ticket IDENTIFIER: direction` path in `ManagerChat`, `ScopeAmendment`, and the existing durable planning/carryover workflow. Only the current authenticated reporting-chain message authorizes an amendment. The Producer records the exact source snapshot, authorizing turn and bounded text replacements, obtains a Technical Director proposal, and validates unchanged ticket hierarchy, dependencies, role ownership, unrelated criteria and completed work before cancelling/carrying over the old sprint. Running/dispatching work and other pending approvals prevent cancellation. The replacement must pass normal estimates and QA readiness.

`RefreshAmendedDeliveriesAsync` re-finalizes existing delivery specifications from revised canonical planning; otherwise the developer's derived brief would retain old acceptance criteria. Repository, base branch and quality-gate controls remain intact. Conflicting legacy briefs block on first application and replay. `ScopeAmendmentTests` and `DirectedDeliveryRecoveryTests` cover the authorization, exact replacements, preservation and replay boundaries. The feature does not treat deferred testing as a pass or grant delivery/merge approval.

On 2026-09-27 the owner explicitly removed mobile/physical-device testing as a current gate: no device or device-testing service is available, and unavailable test devices must not block this phase. Track these measurements as deferred, retain available validation and report limitations honestly. Producer 2.11.1 is packaged for this correction; the canonical live scope still requires the normal amendment command after installation. The prior 2.11.0 snapshot was committed during development; deploy 2.11.1 for the final delivery-brief and test fixes.
Live scope-amendment dispatch (2026-09-27): Producer 2.11.1 saved 21 exact replacements but stopped before coordination because the full request plus ten earlier management decisions was 42,834 characters, over the 32,768-character message bound. Producer 2.11.2 `BoundPlanningHandoff` moves the complete text into `coordinationContext` on the existing planning-cycle artifact, keeping start/message/artifact atomic and session replay stable. The saved live case round-trips exactly in a 45,759-character artifact. Technical Director 2.11.9 `PlanningCoordinationMessage` reads that field only on the authenticated counterpart's exact cycle/package turn, rejects malformed/oversized context, and distinguishes explicit owner amendments from older reference scope. Install both (Director first), then reopen the existing blocked personal planning commitment through the normal UI; do not issue a new amendment with a different authorizing turn or mutate the database. The live sprint remains unchanged until validated planning, delivery refresh, estimates and readiness complete.

`WakeCoordinationWaitsAsync` also handles a ticket-specific personal commitment awaiting a board-wide planning session. When the session has no source ticket, the commitment's `SourceFingerprint` must exactly match an artifact key supplied by that session's authenticated initiator. Existing organization, owner/installation, board, project/team, optional session, claim and timestamp checks still apply. An unrelated fingerprint, target-authored key, non-board session or different explicit source ticket cannot wake the commitment. This is a wake hint only; the Producer still re-reads and validates planning. `CoordinationCommitmentWakeTests` covers terminal events, bounded missed-event discovery, replay and those rejection cases. This platform follow-up requires the next API/AgentHost rebuild and restart; the running process retains its normal timed fallback until then.

### Coordination delayed by administrative runtime and request limits

`AgentRuntimeSettingsService` and `AgentRuntimeManager.TryStartAsync` accept zero for global and per-business active-workload caps, meaning unlimited at this administrative layer. New installations default to zero; existing settings remain unchanged until explicitly saved through Runtime settings. Office resource admission and the single active runtime per installation remain enforced. The Runtime page explains zero, and Communications displays unlimited capacity instead of a misleading zero denominator.

`McpIngressPolicy` exempts only sessions authenticated by `McpAgentSessionService` for the current request from the blanket HTTP request counter. Anonymous, expired, revoked, wrong-token and changed-grant sessions remain throttled by remote address; changing a session header cannot reset their allowance. Request-size bounds, capability grants, work leases, isolation and provider admission remain enforced. The API and AgentHost must be rebuilt/restarted before the settings and authenticated exemption take effect. Then save both administrative caps as zero to remove an existing installation's old 10-global/5-business defaults.

Live verification (2026-09-28): Producer 2.11.2 and Technical Director 2.11.9 completed the saved owner amendment after one scope-preservation correction. Sprint 3 was cancelled with history preserved, all three specialist estimate sessions completed, QA readiness passed, and Sprint 4 became Active. The first implementation stage was dispatched. This confirms planning-to-execution recovery, not completed implementation or final delivery.

### QA runs checks but blocks without saving its verdict

`GameQaReport.CreateTool` in Video Game QA 2.4.7 supplies the confined `submit_qa_report` tool. It accepts typed evidence, checks exact source identity, executed commands, verdict consistency and complete acceptance-criterion coverage, then writes only `.csweet/qa-outcome.json`. Report content never becomes shell code. The destination rejects symbolic links. `GameQaHarness` permits read-only file inspection without unattended approvals while retaining approval for general file writes. `GameQaExecution` diagnoses missing/malformed reports and pending tool approvals explicitly, then retains independent source-integrity checks before accepting any verdict.

The live quality attempt for commit `adbb7acd2be6f9c674cef3c067bdcfd32cb0edf5` ran static checks but its report heredoc hit a shell deny pattern. Its fallback `file_access_ls` call required approval, and the old harness tried to read a report that had never been written. QA 2.4.7 fixes that handoff; it does not turn missing desktop fps or bundle measurements into passes. Install the package and have the owning Producer retry quality stage `b0c24e8d-23af-4be9-b777-981e51958227` through normal bounded recovery. The old failed attempt remains in history.

After the platform restart, the owner-requested global and per-business workload settings were saved as zero through the Runtime UI. This removes the old 10/5 administrative caps; single-installation and Office admission controls remain unchanged.

The subsequent QA 2.4.7 attempt reached `submit_qa_report` five times but supplied flat named fields with JSON-encoded boolean/array strings. The nested report schema rejected those arguments before writing the report, producing generic tool failures and ultimately `task.repeated_issue`. This was a report-binding failure, not a successful QA verdict. QA 2.4.8 `GameQaReport.ReportFunction` exposes flat arguments, normalizes exactly one JSON encoding layer, retains nested-object compatibility, and returns specific validation feedback plus the required criteria. It rejects unknown/missing fields and preserves failed verdicts and evidence. `GameQaReportTests` now covers actual serialized tool arguments through the full harness, including a rejected submission followed by a corrected failed report; 46 tests pass on Windows and Linux. Install 2.4.8 before normal Producer recovery; do not erase the failed attempts or waive desktop performance/bundle evidence because tooling is unavailable.

`WorkOrchestrationService.RetryAsync` allows the current stage assignee or accountable board manager to reopen a blocked/failed stage after re-reading assignment revision and remaining policy attempts. The `task.repeated_issue;retryable=false` marker stops automatic retries; it does not independently prohibit this explicit recovery path. A package update alone does not call that path. QA 2.4.8's self-test, NuGet package version and Linux `.csab` were also verified before publication handoff.

Live recovery (2026-09-28, 10:00 Pacific): the published QA 2.4.8 bundle at source commit `3d30ebc7b0fa7d876d05e280c5c2e735725f6de9` was installed through Settings. The Producer accepted the authenticated `Retry ticket VG4CC32F61E0-22` request, and the board confirmed quality attempt 3 Running with zero attention flags. This confirms redispatch only; the report and final delivery remain unverified until the attempt completes. The execution dialog can still display the previous attempt's summary next to the new Running status.

At 10:07 Pacific, quality attempt `0a859ad6-055a-4a17-b204-744f76447559` completed with outcome `failed`, no infrastructure failure code, and persisted command/criterion evidence. The board subsequently routed ticket 22 back to a Running specialist-execution stage. This verifies report submission and QA-to-rework coordination. Real missing desktop FPS/bundle evidence remains. The model also incorrectly treated missing `.git` as an unverified source identity, despite `GameQaWorkspace.MaterializeAsync` checking the broker-authorized snapshot and `VerifySourceUnchangedAsync` checking original hashes again before acceptance. QA 2.4.9 supplies that provenance explicitly in its model context and explains the expected absence of Git metadata. It preserves branch/publication requirements as separate evidence questions; it does not claim the candidate has merged or measurements passed. Version 2.4.9 is packaged locally, with 46 Windows tests, self-test and both package formats verified; live behavior awaits publication/installation.

### Development profile does not supply executable test tools

`AgentRuntimeManager.TryStartAsync` recognizes `software-development-polyglot-v1` to require writable workspace access, but resolves the same `RuntimeGuestImageId`/digest as ordinary agents through `FleetGuestImageRegistry.ResolveAsync`. It does not select the image described in `runtime/software-development-polyglot-v1/README.md`; that Docker-era document refers to an option absent from the current runtime options. The current launch path uses a certified Office guest VM. Thus the profile declaration alone does not establish Node/npm/browser availability. Live developer and QA reports lacked those tools and could not produce required desktop performance and bundle measurements. Repair must supply and verify appropriate tools through the current certified guest or authorized toolchain execution path. Do not switch agents to Docker, grant host tool access, or substitute static checks for the missing measurements. The existing Node TypeScript toolchain service executes managed build recipes, but is not automatically invoked by the assigned QA harness.

## Service crashes: automatic restart and crash capture

`CSweet.AppHost` registers `ResourceCrashSupervisor` (`src/CSweet.AppHost/ResourceCrashSupervisor.cs`) for
`agenthost`, `api`, `workerhost`, `executiongateway`, `githost` and `provisionerhost`. When one of them
exits with a non-zero code that was not requested from the dashboard (a dashboard **Stop** is never
overridden), the supervisor:

- writes `%LOCALAPPDATA%\CSweet\crashes\<resource>-<UTC time>Z.log` with the exit code (decimal and hex,
  for example `0xC0000005` = access violation), start/crash times and the last 400 console lines;
- restarts the resource after 5s, 10s, 20s ... (capped at 2 minutes), at most 5 times per 30 minutes,
  then pauses automatic restarts and logs that it gave up.

Durable work leases recover on their own once AgentHost is back; the interrupted attempt is retried.

The supervised projects also get `DOTNET_DbgEnableMiniDump=1` and `DOTNET_DbgMiniDumpType=2` (minidump with
heap) via `WithCrashDumps`, writing `%LOCALAPPDATA%\CSweet\crashes\dumps\<resource>-<pid>-<time>.dmp`. The three
newest dumps are kept. Analyse one with `dotnet-dump analyze <file>` (`clrstack -all`, `pe`) or WinDbg (`!analyze -v`).
If no `.dmp` appears for a native crash, enable Windows Error Reporting local dumps once from an elevated
PowerShell:

```powershell
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\CSweet.AgentHost.exe'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key DumpFolder "$env:LOCALAPPDATA\CSweet\crashes\dumps" -Type ExpandString
Set-ItemProperty $key DumpType 1 -Type DWord   # 1 = mini, 2 = full
Set-ItemProperty $key DumpCount 3 -Type DWord
```

Event Viewer > Windows Logs > Application also records the faulting module for native crashes
("Application Error" and ".NET Runtime" entries at the crash time).

## OnDemand runtime holds capacity after completing a ticket

`AgentRuntimeManager.EnsureRuntimeQueuedAsync` gives every OnDemand runtime an idle deadline,
including non-interactive ticket dispatch. `AgentRuntimeManager.ReleaseIdleRuntimeAsync`
recovers missing deadlines from session establishment, interactive requests and completed work.
It preserves ready inbox work and leased work, including inference waits. Periodic
`AgentAttentionEvents.ReviewDue` completion and `PlatformLlmJobService.AccountWaitAsync`
bookkeeping do not extend an otherwise idle runtime's lifetime. AlwaysOn and Scheduled
runtimes retain their separate activation policies.

Idle teardown and `AgentWorkInbox.ClaimCoreAsync` share PostgreSQL claim admission's
transaction lock. Stopping and broker-session revocation commit before remote teardown;
new work remains queued for normal OnDemand recovery. Confirmed asynchronous idle shutdown
retains Cancelled status and its idle reason rather than reporting a runtime failure.
`AgentRuntimeManagerTests.MissingIdleDeadline_ReleasesOnlyIdleOnDemandRuntime`,
`IdleRelease_WaitsForConfirmedRemoteStopAndDoesNotRepeatStopTransition` and
`AgentWorkInboxTests.IdleShutdownFencesClaimsWithoutConsumingPendingWork`
cover deadlines, activity, active work, review bookkeeping, replay and delayed teardown.
Rebuild/restart WorkerHost and AgentHost to apply ongoing automatic cleanup and claim fencing;
no migration or agent package change is needed.

October 15 Block Cascade recovery: all nine Office slots were occupied by eight AlwaysOn
managers across two businesses and the idle OnDemand QA runtime. The targeted idle-release
service call fenced Rohan's runtime, Office confirmed teardown at 09:06:21 Pacific, and
Adrian claimed scaffold ticket `VGF99E0D5782-7` at 09:06:34 Pacific. The running older
WorkerHost classified its asynchronous stop recovery as Failed; the completed plan and QA
evidence were preserved. This live recovery verifies the handoff, separately from rollout
of the source correction above.

## QA waiting behind incomplete runtime shutdown

`OfficeGatewayService.ReconcileStopsAsync` (`OfficeGatewayService.Stops.cs`) uses the existing authenticated heartbeat to rediscover up to eight retired attempts without shutdown acknowledgement. Discovery rotates and repeats across reconnects. `OfficeWorker.ReadControlMessagesAsync` retains every received epoch and handles the teardown-only hint, while `AssignmentStopJournal` retains permanent replay tombstones and reports confirmed cleanup through the existing stop outbox.

`RuntimeHostAuthorizationGate.Attempts.cs` retains exact creation intent and handles; `RuntimeHostRequestDispatcher.Attempts.cs` serializes creation against teardown and recovers lost responses. Hyper-V's `HyperVAttemptJournal` and `HyperVHelperController.DestroyAttemptAsync` recover interrupted creation using provider-owned identities through the existing typed destroy operation. `FleetAgentWorkloadRunner.InspectAsync` still requires stop evidence for every attempt before replacement. Unknown historical creation outcomes remain unconfirmed.

Deploy Headquarters with Office.Contracts 0.9.0 and Office 0.8.0 (Node, RuntimeHost and digest-pinned helper together). Source builds and mocked tests do not certify live Hyper-V recovery. No database reset or fabricated `StoppedAt` is required.

`WorkItemMutationEngine.CoordinationWake.cs` also correlates typed role-estimation and QA-readiness requests through their exact `PlanningDigest`, since their artifact keys identify the narrower request. Initiator identity, installation, project, team, board, terminal status and durable wake receipts remain required. Completion releases only an availability hint; the Producer re-reads current scope and evidence before progressing. Tests: `AssignmentStopJournalTests`, `RuntimeHostRpcIntegrationTests`, `ExecutionWorkloadOrchestratorTests.GatewayRediscoversRetiredAttemptsEvenWhenLatestAttemptHasStopped`, and `PersonalTodoServiceTests.Planning_cycle_recovers_intake_context_and_completion_before_deferral_only_with_exact_provenance`.

## Single QA agent blocked by recursive QA report review

SDK 3.62.0 HierarchicalProjectDelivery assigns QA-owned Artifact reports to the board manager for exact-criterion review, while aggregate QA excludes product authors. WorkDeliveryService.ValidateAssignmentsAsync allows report ownership only with separate board-manager review; code and other artifact authors remain ineligible for aggregate QA. Producer 2.19.3 executes ArtifactDeliveryReview and converts DeliveryStaffingException into an actionable staffing gap. Rebuild/restart the platform and upgrade the Producer before retrying its blocked staffing/preflight through normal recovery. This does not prove the live sprint is ready, remove other missing roles, or waive QA evidence.

The October 11 Production Sprint 1 preflight still failed with Producer 2.19.3 because
`SpecialistAgent` published QA-owned report planning with the default `DeliveryKind = Code`.
Producer 2.19.4 explicitly records `Artifact` for these proposals and uses
`QaReportPlanning.RepairQaReportPlanningAsync` before `StaffWorkflowAsync` calls hierarchical
delivery. The current board manager repairs legacy unstarted report planning with revision checks,
preserving criteria, dependencies, provenance and ownership. Bound deliveries, active/historical
executions and terminal tasks are preserved. The regression suite exercises the actual SDK
staffing path with one QA author, separate manager report review, aggregate QA and replay.
The owner must push the Producer repository, let its release CI finish, install 2.19.4, then
retry the existing preflight/recovery. Verify report planning and delivery become Artifact,
the report quality reviewer is the Producer, and product QA remains independent. A later
staffing or certified-build blocker remains a separate prerequisite; this correction does not
waive it. Local package tests do not establish live sprint activation.

### Shatterbeat startup and staffing stall (October 14, 2026 Pacific)

Read-only inspection of project `281589c5-ec48-45a1-8893-e6371a262ffb` found Production Sprint 1
still Planned, with no delivery plan or sprint execution. Naomi's incident reporting found real
prerequisite failures; it did not cause the stall.

- The connected node `fd8c6cef-4c78-4f35-86cf-8509d36bc46c` reports Office **0.9.0**.
  Its latest staged `office-bundle.json` also records 0.9.0. The guest certification created on
  this host at 13:32 Pacific instead records **v14**, protocol **1.0**, and digest
  `sha256:f51a9b4bd6163aaa1ec8079f389ad429fbb9ec5f04fc8dcfefa9a62cb2c446a0`.
  Fleet inventory agrees and advertises no public-internet support. Office binary version alone
  does not establish development-profile readiness. Adrian 4.0.0 and Rohan 3.0.0 request
  `software-development-polyglot-v1`; their snapshot contained 431 and 416 failed starts respectively.
- Producer coverage request `20100ae2-95d9-4182-b560-fbbb9683148e` was RevisionRequested because
  its trusted flow-metrics source revision was stale. The approved roster contains Chief,
  Creative Director, Producer, Technical Director, Engineer and QA; the published asset work
  also requires artist, audio and level coverage. Hierarchical recovery returned a staffing
  exception before revisiting that coverage request. The generic author-independence wording
  does not establish a review conflict: this failure occurred while selecting an author.
- Naomi's current asset decision selects procedural production and is bound to the accepted
  vision. Producer `ResolvedPlanningDirections` carried only management-direction decisions
  into decomposition, omitting this strategy. Existing sprite-sheet and track deliverables
  therefore reached planning without the selected generation method. Procedural production
  does not itself waive quality criteria or prove every specialist is unnecessary.

Local corrections:

- `Start-CSweetDevelopmentOfficeSetup.ps1` now requests `DevelopmentTools` and the certified
  IPv4 development variant. `Assert-DevelopmentOfficePayload` checks protocol 1.1 and v15/v15-ipv4
  before installing services, including for a downloaded bundle. Actual certification remains
  mandatory; no certification values or internet grants are fabricated.
- `AgentRuntimeGuestSelection.ResolveAsync` owns both eligibility and launch selection, retaining
  configured provider/digest pins. `AgentRuntimeEligibilityService.EvaluateAsync` reports an exact
  environment prerequisite before queuing runtimes. Existing pending-work discovery can resume
  after compatible inventory appears; unavailable images no longer cause repeated launch records
  through the ordinary on-demand path. Other startup failures still need their own recovery policy.
- Producer **3.1.2** `ReadAssetPlanningDirectionsAsync` rereads current project decisions and
  exact accepted vision revisions before carrying the full strategy into the existing authenticated
  planning handoff. A changed direction digest invalidates reuse of an older planning collaboration.
  `SubmitCoverageAsync` gives stale metrics one bounded evidence correction with stable replay,
  preserving the approved staffing baseline. `StaffWorkflowAsync` revisits uncovered task roles
  after a hierarchical staffing failure. Hiring and spending remain separately authorized.

Verification: 362 Producer tests with sibling SDK references disabled, Producer self-test,
106 platform runtime/lifecycle tests in the separate `ShatterbeatVerified` configuration,
and `scripts/tests/Test-DevelopmentOfficePayload.ps1` (six real guard cases) passed. The packed
Producer NuGet manifest, SDK 4.1.1 dependency, version 3.1.2 and release heading were inspected.
An initial sandbox testhost run could not connect; a default-output platform build encountered
running-service file locks. The successful isolated verification did not stop or replace services.

Rollout remains unverified: deploy/restart the platform and install Producer 3.1.2, drain the
Office through supported maintenance before replacing its payload, certify the development
guest and verify current advertised protocol/image/capacity. Then reconcile the existing
planning with the asset strategy, resolve genuine remaining coverage through normal resource
operations, recover expired estimate/readiness sessions and verify first implementation plus
independent review. No live project records, grants or assignments were changed during this work.

### Reviewed authoring internet and Node dependency recovery

October 14 Office update follow-up: session `f0469177-8c3d-4712-99ce-2bf8dd1bdfca` failed
during `RunInternetSmokeAsync` before installation, with guest-channel EOF. The downloaded
0.9.0 hosted-image build never called `Prepare-CSweetDevelopmentPayload.ps1`, leaving out the
fixed TUN adapter and development tooling. The initializer flags correction exposed this
separate packaging defect; it did not establish live readiness.

Office 0.9.1 `build-hosted-hyperv-image.sh` stages the checksum-pinned payload through the
existing provisioner. `Publish-HostedOfficeAssets.ps1` binds the profile declaration to the
finished image hash and carries profile/protocol metadata into support and full archives.
C-Sweet `Resolve-CSweetOfficeRelease` skips ordinary-only releases before downloading and
certifying them, using the existing configured source path when no compatible release exists.
Checksum, trust and certification failures do not become fallback permissions. The source
preflight reports its actual version instead of the former hardcoded 0.5.0.

The installed Office and identity survived the failed update and remain paused. Retry uses
normal Office care with a fresh authorized setup session after validating preparation. The
published 0.9.0 assets are immutable; 0.9.1 source/archive verification is not a published
development image or live certification result.

Source preparation then passed on October 14 at 15:50 Pacific: Office 0.9.1, guest protocol 1.1,
v15-ipv4 certification, digest `sha256:d2301fccc3692f1de9945e96cf943d5b31e24b6246b3490741b8dad7f1f1f634`.
Runtime isolation, real brokered builds, public TCP/UDP, denied private/host access,
Node/npm dependency execution and network lease cleanup passed. IPv6 remains unavailable under
this mode. All 929 payload hashes were verified. In-place source updates now leave the separate
Compute image preparation to its own on-demand workflow instead of making it another Office
upgrade dependency. The image preparation was performed with installation disabled; the
subsequent identity-preserving Office care session remains the authority for deployment.

The supported Office care update completed October 14 at 18:44 Pacific using session
`87460f8b-ee53-4140-8b69-6ee708d98353`. The existing Office identity
`fd8c6cef-4c78-4f35-86cf-8509d36bc46c` reconnected on 0.9.1, advertising broker protocol
1.1, v15-ipv4 certification, the same verified development digest above, and available runtime
and public-internet capabilities. Office care verified the connection; its Resume work action
restored Ready status. This verifies the live Office repair, not deployment of Producer 3.1.2
or completion of Shatterbeat's remaining planning and staffing recovery.

`runtime.internet.public.v1` adds reviewed public egress through the Office Node. `AgentRuntimeManager` now resolves the certified development image for `software-development-polyglot-v1`; it cannot silently use the old ordinary image. Follow `features/runtime-public-internet.md` for pinned payload preparation, v15 real-VM certification, independent package validation and the deployment gate before retrying a missing-Node ticket. Earlier incident reports above describe older deployments and do not establish recovery of the current runtime.

### Block Cascade scaffold review context failure (October 15)

Scaffold publication `f9752b9d-7eaf-4cd3-bfe4-0a01ed457358` included the generated Phaser bundle in
`dist/assets/index-B5DwHTft.js`. The technical-review request contained 1,824,859 message characters
and the model refused it for exceeding context capacity. This was review-input packaging after a
completed implementation; no technical approval or independent QA had been obtained.

See `DeliveryTaskTechnicalReview`, `BranchDiffTechnicalReview`,
`GitWorkspaceCapabilityHandler.ReadReviewDiffPage` and
`InternalGitRepositoryStore.WriteSnapshotTreeAsync` for the SDK 4.2.0 reference/paging and gitignore
correction. `BranchDiffTechnicalReviewTests`, `MergeDecisionPersistenceTests`,
`GitWorkspaceCapabilityHandlerTests.DiffPagesAreBoundedAndRejectChangedPublicationSourceTargetOrPatch`
and `InternalGitPublicationTests.SnapshotPublicationHonorsNestedGitignoresAndKeepsPreviouslyTrackedFiles`
cover real callbacks, current-reference checks, bounded complete coverage and actual Git behavior.
Roll out the platform and matching agents before retrying only the blocked review stage. Source tests
and local packages do not upgrade the installed agents or establish live recovery of this candidate.

### NEON BREAK technical review rejected by the gateway (October 15)

Ticket `VGF0E67F20A5-11` completed implementation, then failed technical review with
`$.includePatch is not allowed`. The retry at 13:16 Pacific used Technical Director 3.1.2 and
failed before the handler or model. The nine remaining executable tasks depend on this ticket.

`McpToolCatalog.InputSchema` now accepts the SDK's serialized `ReviewGitMergeRequest`, including
`includePatch` and nullable `diffRead`. Nested page identities, SHA/digest shapes, offsets and
the 12,000-character limit remain strict; unknown fields remain rejected. `GitReviewSdkSchemaTests`
uses `McpGatewayEndpoints.ValidateToolInput`; `MergeDecisionPersistenceTests` now crosses this
validation before calling the handler, using the SDK's actual camel-case serialization.

Technical Director 4.0.0 `ExecuteCapabilityCoreAsync` always uses `DeliveryTaskTechnicalReview`
for canonical task review. `WorkOrchestrator.DispatchAsync` sends `deliverySpecificationJson`,
not a typed `WorkItem.Delivery`; the removed routing test silently selected the old full-patch
reviewer. The shared reviewer rereads current delivery and independent assignment from the
authorized work API. The obsolete reviewer, custom decision parser/receipts, standalone
merge-decision execution/delegation and unused merge-authorization grant are removed. Trusted
integration and independent QA remain mandatory. Existing malformed/stale assignments fail
closed; no compatibility adapter or ticket-data rewrite was added.

Verification: 50 platform schema/review/integration tests and all 126 Technical Director tests
passed. The agent was tested against published SDK/contract packages, self-tested and packed as
4.0.0. AgentHost was stopped through Aspire, rebuilt in its existing Debug deployment with
dependency builds disabled, and started with the schema correction. The new agent package still
needs its normal release/import before retrying only technical review; completed implementation
and its publication remain intact.

The owner-requested schedule maintenance uses `AgentInstallationService.UpdateScheduleAsync`
with the existing audit writer and data-protection keys. Eight active Chief/Creative Director/
Producer/Technical Director installations across two businesses were selected by exact agent IDs
and changed to OnDemand. Their 300-second cadence, Skip overlap, enabled state and grants were
preserved. The service enforces the existing global 600-second maximum, so their old 86,400-second
schedule budgets were normalized to 600 seconds. The older YouTube Account Manager, Discord and
other agent types were outside the selection. Global definition defaults were not changed.

### NEON BREAK model reversed the review commits (October 15)

The 13:50 Pacific retry used installed Technical Director 4.0.0 and successfully read the current
item, exact-publication reference and first 12,000-character diff page through the gateway. At
13:51 it blocked with `A diff review did not preserve exact commit and page identities.`
The model returned source `d32ae70e3eb752b64daae261b1552493946bc203` and target
`51ac4a252d6c86632ec0b1288c982c94c003ab6d`: the inverse of the authorized pair. The five page
findings were not accepted as a completed review; no merge or QA approval occurred.

SDK 4.2.1 `BranchDiffTechnicalReview.ExecuteAsync` separates model review content from SDK-owned
provenance. Models return page summaries/findings and the final substantive decision. After each
authorized page passes publication/source/target/digest/coverage validation, the SDK binds its
response to those exact commits and offset. Synthesis receives those ordered bound reviews and
the SDK attaches the exact pair to its final result. Neither copying nor interpreting hashes is
a model task. Current-candidate checks, full coverage and the unresolved-page-finding approval
guard remain enforced. Technical Director 4.0.1 pins the fixed SDK; release the SDK first, then
release/install the agent before retrying only the blocked review stage.

Verification: all 431 SDK tests, two HelloAgent tests, seven generated-template tests and the
template self-test passed. Technical Director's 126 tests and self-test passed with sibling SDK
and work-contract references disabled, consuming the packed SDK 4.2.1. The platform SDK pin is
4.2.1 and 50 gateway/paged-review/persistence/task-integration regressions passed using that
package (existing Memory sibling references remain necessary). Both `.nupkg` manifests were
inspected: SDK 4.2.1; Technical Director 4.0.1 depending on SDK 4.2.1 and work contracts 4.0.0.
Packages remain local release artifacts. This investigation did not requeue the ticket, deploy
the new agent or change its approvals, schedules or evidence.

### NEON BREAK completion plan after malformed page review (October 15)

The 14:13–14:17 Pacific run used Technical Director 4.0.1 / SDK 4.2.1. It read the
unchanged publication `1c6752f4-dd07-4a5a-bd85-e318f1b9c356` at offsets 0, 12000,
24000 and 36000. The first three responses parsed and retained 6, 7 and 2 findings.
The fourth response ended in `]}}`. Offline `JsonDocument.Parse` reproduced
`'}' is invalid after a single JSON value. Expected end of data.` The provider
reported `stop` and 1,436 output tokens, so this was malformed completed output,
not evidence of context/output exhaustion. The task remains at technical-review;
there is no integration receipt or independent QA result.

The identity-binding correction worked. Remaining confirmed code/configuration
issues and forward acceptance work are:

1. **Structured review output and actionable diagnostics.**
   `BranchDiffTechnicalReview.ExecuteAsync` directly deserializes every page and
   final response. Introduce a validated review-content contract, one bounded
   correction for malformed output on the failing page, and explicit page/schema/
   provider-finish diagnostics. Do not trim braces, fabricate a verdict, parse
   reasoning as a decision or rerun successful pages. Enforcing JSON schema at
   the provider needs a real transport change: `PlatformChatClient` does not
   transmit `ChatOptions.ResponseFormat`, and `PlatformLlmCapabilityHandler`
   does not apply it. Its saved installation output budget also overrides SDK
   per-call caps intentionally; a prompt-only cap is not a transport guarantee.

2. **Complete, resumable review within evidence budgets.**
   This patch has 146,391 characters (13 pages); `package-lock.json` accounts for
   80,268. Reviews live only in a local list and serialized page reviews have a
   32,000-character synthesis ceiling. Overflow is a forecast, not the latest
   observed failure. Use bounded summaries, durable full findings, deduplication
   and explicit cross-page concern resolution. Persist validated progress with
   the publication/source/target/digest, planning/scope revision and review-policy
   identity; reauthorize and recheck freshness on resume. All diff coverage must
   remain accounted for. Current TD runtime is OnDemand with 86,400 seconds after
   upgrade, stage timeout is 3,600 seconds; the latest failure was not a timeout.
   `AgentDefinitionInstallationSynchronizer` resets the runtime budget from the
   definition on update, so do not diagnose this run using the prior 600-second
   maintenance value.

3. **Separate defects from observations and missing page context.**
   The accepted page responses include literal `No defect`, `No action required
   at Phase 0`, optional future hardening and questions answered on other pages
   inside `findings`. The current final guard rejects approval whenever any page
   has any finding, so synthesis cannot resolve these concerns. Use explicit
   blocking findings with evidence/criteria, separate nonblocking observations
   and cross-page questions, and require an evidenced disposition for every
   blocking finding. Preserve the ban on silently dropping actual defects.
   A model claim that Node 22 necessarily crashes on ESM `.js` without a package
   type is not established: Node 22.7 enabled syntax detection by default
   ([Node documentation](https://nodejs.org/download/release/v22.14.0/docs/api/packages.html#syntax-detection)).
   Its advice to remove the second Docker build also omits the required image
   export: build-push-action defaults `load` to false
   ([action documentation](https://github.com/docker/build-push-action)).

4. **Resolve repository/CI acceptance against the actual provider.**
   The assigned repository is Ready/Connected InternalGit with an active team
   policy. Accepted planning and candidate documents still describe it as
   Pending/null, while criterion 2 requires default-branch protection and CI
   building/deploying on main. The candidate only defines GitHub Actions/Pages.
   A workflow file and local `npm run build` do not establish that live criterion.
   Recommended MVP scope: retain InternalGit and use the approved platform
   build/compute-preview path, with a normal approved planning/scope amendment
   defining exact pipeline/deployment evidence and when it must exist. If literal
   GitHub CI remains required, provision its supported automation separately;
   moving managed delivery to GitHub also encounters the existing provider
   atomic-ref/protection limitation. Do not replace approved criteria silently
   or require release-to-main before the dependent implementation can start.

5. **Fix independent QA's current-state/result integration.**
   `WorkOrchestrator.DispatchAsync` sends raw `deliverySpecificationJson`.
   QA 3.0.0 `GameQaExecution.ExecuteCapabilityCoreAsync` reads
   `assignment.Item.Deserialize<WorkItem>()?.Delivery` after executing tests.
   That is null for the actual dispatch shape, so it emits `GameQaOutcome`
   instead of hierarchical `WorkDeliveryReviewResult`. Platform
   `WorkOrchestrator.RecordQualityValidationAsync` requires `CandidateDigest`,
   `Approved`, criterion evidence and repository-bound validations for the exact
   completed integration receipt. Reread the authorized typed current item and
   validate assignment/planning/delivery before tests, then emit that single
   canonical hierarchical result. Preserve independent reviewer and source
   immutability checks; no permissive platform result adapter.

6. **Make fresh QA dependency restoration possible.**
   QA 3.0.0 already requests the development image but its installed grant has
   no `runtime.internet.public.v1` and no network rules. Engineer 4.0.1 has that
   reviewed permission. A fresh source-only QA snapshot needs an authorized npm
   dependency path: reviewed public registry egress or certified offline inputs.
   Add the appropriate request and normal grant review, then verify denied
   private/host access still holds. The missing permission is confirmed; an
   actual QA `npm ci` failure has not occurred because review never completed.

7. **Rework the candidate against verified findings and task scope.**
   Route actual rejected findings through the existing reviewed author loop.
   Validate malformed URL handling in `deploy/server.js` (uncaught
   `decodeURIComponent`), missing-asset 404 behavior, and browser/static asset
   loading instead of accepting `/healthz` alone. The current 12 unit tests cover
   analytics, chiptune data and the governor, not CI activation or browser load.
   Check the demo's first-click analytics opt-in against the locked consent
   requirement and keep optional worker/caching changes proportionate to Phase 0.
   Verify the proposed Docker smoke test uses `load: true` if removing its plain
   Docker build, and test the exact chosen runtime. Record TD feasibility through
   canonical review evidence; document checkboxes must not create extra gates.

8. **Exercise the complete production handoff before another owner retry.**
   Use the actual raw assignment, SDK/broker serialization, all 13 authorized
   pages, malformed fourth-page output, final review, reviewed rework, trusted
   InternalGit integration and the production QA callback/result validator.
   Test page resume, stale refs/planning, duplicate delivery, overflow, unresolved
   findings, lost integration responses and QA infrastructure inability. Published
   package consumers must disable sibling references; bump/pack SDK and every
   changed agent and verify release versions before normal installation.

Completion is the policy's actual path: approved Technical Review → trusted
task integration receipt → independent QA of the exact integrated story commit
with all three accepted criteria evidenced → Done. A substantive rejection must
return the author to its preserved candidate before integration, or current story
state after integration; `ResolveReworkBaseAsync` already implements that choice.
The task's revision and candidate evidence must survive those loops. Then verify
task 12 becomes eligible; the sprint currently has nine Pending items behind the
blocked first task. Full story/release acceptance remains separate.

This section is an investigation and proposed implementation plan. Only read-only
live diagnostics and local response parsing were performed; no new product fix,
grant, scope amendment, retry, merge or deployment was applied for this analysis.

### Implemented review/QA recovery and local verification (October 15)

The follow-up implementation is documented in
[technical review and QA recovery](features/work-management/review-qa-recovery.md).
Final local packages are SDK 4.3.2, Technical Director 4.0.2, Engineer 4.0.2 and
QA 4.0.0. The review schema/finish reason reaches the provider; accepted diff pages
survive retries; every blocker needs an evidenced disposition; canonical QA reads
and results match actual dispatch. `WorkDeliveryService.AmendTaskAsync` provides a
revision-checked, manager-authorized way to replace obsolete task criteria while
preserving candidate code and accepted source identities.

SDK 427 tests, two sample tests, seven generated-template tests, TD 126, Engineer
245, QA 84, 129 focused platform tests and the connected real-InternalGit agent
journey pass. The connected journey uses scripted models/broker transport, actual
production callbacks and actual authored-fixture commands; it is not live
installation/provider/browser certification. The ticket was not retried. Deploy
the platform, install the verified agents, review QA egress, amend the paused scope
through the authenticated API, and complete installed-runtime checks before the
explicit author retry described in that document.

### Prism Break artifact QA rework access denial (October 15)

See [artifact QA rework recovery](features/work-management/artifact-qa-rework.md).
The second QA pass on `VGD0CEEB9048-8` requested the superseded document because
unordered prior outcomes were treated as chronological history. A separate
partial-tracking bug revoked the new document's grant when QA claimed work.
`WorkOrchestrator.AssignmentOutcomes`, SDK `ArtifactDeliveryReview` and
`WorkOrchestrationBoardState.SynchronizeTaskArtifactGrantsAsync` now bind current
producer evidence and preserve grants across a fresh claim context. SDK 4.3.3 and
QA 4.0.1 require deployment before the normal explicit quality-stage retry.
