# Workspace sync schema recovery

## Confirmed failure

Breakout Resonance passed QA sprint-readiness and Production Sprint 1 became Active
after the saved-outline recovery. Its first executable ticket, `VGCD352DE8BC-13`
(`6fa3ddfd-8ce7-4844-b756-2c691a6fd240`), stopped at 08:16:29 Pacific with
`JSON Schema validation failed: $.commandClass is not allowed.`

This was valid SDK request JSON, not malformed model output or a recurrence of the
planning character-limit/digest failures. `PlatformGitWorkspaceClient.MaterializeAsync`
serializes `GitWorkspaceSyncRequest` with the optional `CommandClass`, `InputFingerprint`
and `ResumeSummary` properties, including nulls on an ordinary pull. The trusted
`GitWorkspaceCapabilityHandler.SyncWorkspaceCoreAsync` already supports those properties,
but `McpToolCatalog`'s strict input schema omitted all three. Gateway validation rejected
the request before the workspace handler ran. Removing only null properties would leave
checkpoint upload and resource-recovery requests broken.

## Correction and verification

`McpToolCatalog.InputSchema` for `GitWorkspaceCapabilities.Sync` now admits the complete
SDK request. It retains `additionalProperties=false` and validates supported command classes,
64-character hexadecimal input/source fingerprints, at most 16 validation receipts and
commands bounded to 512 characters. Nested summaries and validation receipts also reject
undeclared fields. Existing assignment, ownership, publication and checkpoint authorization
remain in the handler.

`WorkspaceSyncSchemaTests` serializes actual SDK download/upload request types, including
the default null fields that reproduced the live failure. The ordinary download and four
checkpoint command-class cases failed against the old schema. After the correction,
all 68 selected schema, gateway, workspace-handler and workload-resource tests passed,
with no skips. Tests also cover legacy omitted fields, malformed fingerprints, receipt
bounds and undeclared fields. No SDK or agent implementation/package changes are needed.

## Local recovery

The tested AgentHost binary was installed during a verified idle window, with hash checks
and rollback copies. The API and accepted planning artifacts were preserved. The existing
ticket was retried through `WorkOrchestrationService.RetryItemAsync` using the owner's
application identity and current revision. The original failed attempt remains in history;
attempt 2 was queued at 09:33:59 Pacific in the same active sprint.

At 09:34:30 Pacific the workspace pull succeeded with the exact same 245-byte request
and SHA-256 `326E3D10043F5B326F9B4C00701CCD85EF8E405B57AB1A248DD629C05F197DCB`
that had failed before the correction. A checkpoint push carrying `commandClass=build`
also succeeded, followed by ordinary checkpoint uploads. At 09:35:07 the ticket and
specialist-execution stage were Running, with no current blocker/error, and model/tool
execution had proceeded beyond materialization. Sprint 1 remained Active. These observations
confirm live download and upload compatibility; they do not claim completed implementation,
QA or delivery acceptance.
