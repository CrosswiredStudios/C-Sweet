# Agent harness and provider routing — how it works

How an agent running in its VM reaches platform tools and a model provider, and which controls apply
along the way. Read [harness workspace](harness-workspace.md) for the accepted scope and
[agent governance](agent-governance.md) for posture and approvals.

## 1. From the VM to the platform

```
Hyper-V guest (CSweet.Compute.Guest)          Host
  agent process ──VSOCK/HV socket──► GuestBrokerHostSession ──► IAgentBrokerOperationHandler
                                      (lease, proxy, streams)        └─► AgentHost /mcp (McpGatewayEndpoints)
                                                                          └─► PlatformCapabilityDispatcher ─► handler
```

- **Channel.** `GuestBrokerHostSession` validates the `AgentBrokerGrant`, sends boot configuration,
  then runs a hello → challenge → proof handshake (`GuestHandshakeVerifier`) bound to the workload,
  channel, guest image digest, artifact digest, boot token and expiry. The lease cancels the session at
  grant expiry. No TCP or same-partition fallback exists.
- **Brokered calls.** After the lease, the guest may send `ProxyRequest` envelopes (at most 32
  concurrent). Method, path and headers are validated (`IsMethod`, `IsPath`, `IsHeader`); network-path
  references (`//host/...`) are rejected so a guest call cannot replace the authority. `StreamChunk`
  and `Exit` envelopes carry output and a sanitized exit reason (`GuestWorkloadExitedException`).
- **Tools.** The agent's MCP client talks to `/mcp`. `McpToolCatalog` lists tools for the session's
  **approved grant** only (`Grant.RequiredCapabilities`), plus the implicit, read-only
  `McpToolCatalog.SelfReadCapabilities` every agent gets: `read_agent_governance` and
  `read_harness_status`. The dispatcher uses the same set to exempt them from grant and setup-context
  checks, so the two lists cannot drift.

## 2. Capability dispatch (every tool call)

`PlatformCapabilityDispatcher.InvokeAsync`, in order:

1. Setup-context policy (`PluginSetupAssistancePolicy`) and project policy (`ProjectCapabilityPolicy`).
2. Grant check: the capability must be in the approved grant, or be implicit
   (`IsImplicitPlatformCapability`).
3. Risk class from `CapabilityRiskCatalog` (Read / Write / Destructive / Outward).
4. Posture (`IAgentPostureReader`): **Plan** refuses mutations; **Manual** admits a durable approval
   for the exact request (`IAgentCapabilityApprovalService.AdmitAsync`) and returns `Pending` until a
   human decides; **Auto** executes. Approved results are stored and replayed, never re-executed.
5. Temporal policy for Destructive/Outward risk (`IAgentPolicyService`): a shared, cross-host window
   (3 destructive or 60 outward per hour).
6. The handler runs.

## 3. Model inference and routing

Agents call `platform.llm.chat-stream.v1` (directly or via the queued job service,
`PlatformLlmJobService`). `PlatformLlmCapabilityHandler.StreamAsync`:

1. **Stage.** `HarnessRuntime.ResolveStageAsync` derives the stage from durable dispatch
   (`WorkExecutionAttempts → StageExecution.StageKey`), never from the request. Advisory judgments use
   the trusted `Judgment` stage.
2. **Route.** `HarnessRuntime.SelectRoute` picks the stage route, else the `Default` route, else the
   caller's provider/model. Saved routes are the approval: `HarnessRuntime.Approves` admits a model
   only if it is the provider default, a saved route or fallback target, or the installation's
   configured model.
3. **Skills.** Pinned `BusinessSkillRevision`s are appended to instructions. A revoked skill, or one
   needing a capability outside the grant, stops the request.
4. **Context.** With `CompactContext`, exact duplicate tool results shrink
   (`HarnessContextOptimizer.Compact`). If the agent also has `platform.harness.context.read`, results
   over 16,000 characters are replaced with a preview plus a pointer to the **`read_historical_context`**
   tool, which pages the protected original from the same active work attempt only.
5. **Dispatch fence.** `ProviderDispatchScope` re-authorizes on every HTTP attempt: provider
   fingerprint, installation/grant revision, running runtime and tick, lease and deadline of the work
   attempt, memory evidence, benchmark pinning and harness policy revision (`HarnessRuntime.ValidateAsync`).
6. **Budget.** `HarnessBudgetSession` reserves `input upper bound + full output allowance` against one
   shared account per canonical root task (`HarnessRuntime.BudgetKey`), so retries, fallbacks and
   delegated descendants share one ceiling. A descendant may only tighten its owner's ceiling. Only a
   completed attempt with known usage is settled down; failed attempts keep their reservation.
7. **Concurrency.** Each provider has a gate sized by `MaximumConcurrentRequests`
   (`ProviderInferenceGate`). Queued jobs gate on the **routed** primary provider
   (`HarnessRuntime.ResolvePrimaryProviderAsync`).

### Fallbacks

A route may list up to three `HarnessRouteTarget` fallbacks. `StreamAsync` tries the next target only
when **all** hold:

- the failure code is `llm.provider_unavailable` (408/429/5xx, connection loss, timeout);
- nothing has been emitted to the agent yet (a partial answer is never spliced across models);
- no benchmark variant pins the provider/model.

Each attempt gets its own run log (`InferenceSettingsJson.routeAttempt`; the run inspector labels it
"fallback N · Stage" via `RunInspectorModelCall.RouteAttempt`), its own budget reservation
priced at that target's rates, and its own provider slot. A fallback slot wait is bounded
(`FallbackSlotWait`, 30 s), so installations whose routes fall back toward each other cannot deadlock.
Validation rejects repeated targets, more than three fallbacks, and unpriced fallbacks under a monetary
ceiling.

Realtime: harness saves emit `AppRealtimeEvents.HarnessChanged`. `Pages/HarnessWorkspace.razor`
treats it as a wake hint. It re-reads the workspace and, only when the revision or skill set
differs, offers a reload instead of overwriting unsaved edits.

### Failure codes agents see

| Code | Retryable | Meaning |
| --- | --- | --- |
| `llm.provider_unavailable` | yes | Provider outage after all approved fallbacks. |
| `harness.budget_exhausted` | no | The shared task ceiling cannot cover the attempt. The message states needed vs remaining. A manager must act. |
| `harness.policy_changed` | yes | Policy or skills changed while waiting. Retrying reads current policy. |
| `harness.policy_invalid` | no | An assigned skill was revoked or exceeds the grant. |
| `llm.dispatch_denied` | no | Authority changed (grant, runtime, lease, provider config). |

## 4. Harness tools inside the VM

| Tool | Capability | Grant | Purpose |
| --- | --- | --- | --- |
| `read_harness_status` | `platform.harness.status.read.v1` | implicit | Own stage, route, fallback count, compaction, skills, remaining task tokens/cost. Check before large model work. |
| `read_historical_context` | `platform.harness.context.read` | explicit | Page a spilled tool result from the same active work attempt. A snapshot, not current state. |
| `advisory_judgment` | `platform.harness.judge` | explicit | Relevance/Risk/Completeness classification through the Judgment route; cannot approve anything. |
| `read_agent_governance` | `platform.agent-governance.read.v1` | implicit | Own posture and recent approvals after a wake or reconnect. |

## 5. Doctor

`HarnessWorkspaceService.ReadAsync` reports read-only findings: installation, grant, runtime, provider
credentials (every route and fallback provider), Office heartbeat, pending approvals, recovery
checkpoints and judgment validity. Routing findings flag a route with no fallback or with fallbacks on
the same provider, a granted judge with no Judgment route, compaction without context retrieval, and a
task token ceiling at or below the per-call output budget, which would refuse every call. For agents
granted any `compute.*` capability, `ComputeFindingsAsync` reports whether an enabled compute node
has an enabled guest template (`compute:placement`) and summarizes the agent's VMs still holding
resources, including failed ones and their failure codes (`compute:environments`). Doctor never
starts a runtime, wakes a compute provider or makes a paid connection test.

## 6. Known limits

- Provider capability flags (`SupportsToolCalling`, `SupportsVision`, ...) default to false in presets,
  so routing does not enforce them. Choose fallback models that support the agent's tools.
- Fallback applies to platform inference only. Brokered guest HTTP and capability handlers do not retry.
- The PostgreSQL budget and governance acceptance tests need `CSWEET_DELEGATION_TEST_POSTGRES`.
- Native VSOCK/systemd execution and physical VM acceptance are still pending
  (`src/CSweet.Compute.Guest/README.md`).
