# MCP tool output contract recovery

## Confirmed failure

QFN Games (Prism Breaker), 2026-11-11. After initial ticket planning the Technical Director asked for build
capacity with `request_project_resource` (`resource.request.v1`). `ResourceFulfillmentService.RequestAsync` saved the
request and returned one `ResourceRequestState`. The gateway then validated that result against the tool's output
schema, which `McpToolCatalog.OutputFor` hand-listed as an array. Validation threw after the request had been saved,
so the agent saw a failure for work that had happened. It could neither continue (no result) nor retry safely (a
retry asks for the same resource again), and the build-capacity setup stalled.

`resource.decide.v1` had the same wrong declaration. The new SDK cross-check test (below) found one more live
mismatch: `platform.artifact.read.v1` returns one document when the request names an artifact and a list otherwise,
but was declared as one object, so every successful artifact list was rejected.

Root cause: output schemas were a hand-typed list of capability names, separate from the types the handlers return
and the types the SDK reads. Nothing compared them.

## Correction

**One declaration per tool's result type.** `McpToolOutputContracts.Types` maps a capability to the C# type its
handler returns to the agent (`ResourceRequestState`, `IReadOnlyList<ResourceRequestState>`, ...). `OutputFor` and the
generic handler fallback derive the output schema from that type: a collection becomes
`{"type":"array","items":{"type":"object"}}`, `ObjectOrList<TObject, TItem>` becomes `{"type":["object","array"]}`, and
anything else (including an unlisted tool) is `{"type":"object"}`. Only the root kind is checked; record shapes stay
owned by the contract packages. The hand-written `ArrayOutput` list is gone. Connector action tools keep their
explicit full schema, and delegation tools still have no output schema.

**A committed effect is never reported as a failure.** When a successful handler's payload breaks its declared output
schema, `McpGatewayEndpoints.CallToolAsync` no longer throws. It:

- returns the saved result with `isError: false` and
  `_meta.csweet.contractViolation = { code: "platform.contract_violation", message, retryable: false }`;
- logs an error naming the capability, agent, installation, request and the schema violation;
- writes a `platform.capability.contract-violation` audit event (outcome Failed, error code
  `platform.contract_violation`) so the operator can find it in the audit log.

There is no platform-wide operator incident queue yet; the log entry and audit event are the operator record.

**Agent side (SDK 5.22.0).** `PlatformCapabilityResult.Read<T>` reads every typed client result. A result that reads as
the expected type continues (and `McpAgentRuntimeClient` logs the contract-violation warning). A result that does not
read throws `PlatformCapabilityException` with `FailureCode = "platform.contract_violation"` and `Retryable = false`,
whose message says the action already happened and must be escalated, not retried. Older SDKs also continue on a
readable result, because the response is not an error; an unreadable one fails as a generic validation error.

## Verification

`McpToolOutputContractTests`:

- resource request and decide declare one object; read declares a list;
- every declared output schema has the root kind of its declared type, and every declared type belongs to a
  registered tool;
- **SDK cross-check:** every public typed SDK client method (the `PlatformCapabilityClient` sub-clients and every SDK
  class constructed from a `PlatformCapabilityClient`) is called against an `AgentTestRuntime` that answers each
  registry tool with an empty value of the gateway's declared kind. A method that cannot read that root kind fails
  the test. It exercises 181 of the 225 registry tools against SDK 5.20.0 and caught the artifact read mismatch. A new
  tool, or a changed SDK result type, that disagrees with the gateway now fails this test instead of a live run;
- a successful payload that breaks its schema is returned with the non-retryable contract-violation marker; failures
  keep their code and retry hint.

Gateway, catalog, schema and artifact test classes: 1,821 passed. The three failures need sibling agent repositories or
are unrelated (`HierarchicalDeliverySchemaTests`, `ProjectDeliverySetupTests.Published_manager_profile...`,
`ApprovalDashboardServiceTests.DeliveredOpenIncident...`).

## Follow-up

Input schemas are still hand-written JSON in `McpToolCatalog.InputFor` and have drifted from SDK request types before
(`workspace-sync-schema-recovery-2026-10-17.md`, the git `pushedCommitSha` fields). Deriving them from the request
types with `ContractToolSchema.Create`, as the resource tools already do, would close that class of failure too.

## Recovery for QFN Games

Install the AgentHost with this change, then retry the failed build-capacity request: answer Marcus's decision card
with the retry option, or retry the resource request (it reports `canRetry`). Agents pick up the SDK behavior when they
are rebuilt against 5.22.0; the gateway change alone already unblocks the stalled request.
