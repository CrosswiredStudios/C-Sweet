# Account import diagnostics

An unchanged `ImportFailed` view is not evidence that a resume request reached the server or that discovery ran again. Import diagnostics distinguish the saved resume acknowledgment, a worker lease, and the latest attempt result.

`PluginAccountService.ResumeImportAsync` checks the current human, installation, package/profile binding and expected session revision before it saves `Importing` with a new locally generated correlation ID. The response contains the setup ID and saved revision. Existing callers with an empty body remain supported; older UI clients can discard the new JSON response. New clients treat an older server's HTTP 204 as unconfirmed, rather than inventing a receipt.

`PluginAccountSetupWorker` runs in AgentHost. Its existing five-second schedule and lease recovery remain unchanged. A lease increments the attempt number and saves its start time. The import saves a bounded stage before provider or managed-service discovery. Success, known failure and interruption record content-free outcome metadata. A diagnostic write never authorizes provider access, grants a business access, overwrites another lease owner, or replaces the original exception.

The authenticated account-management view adds the session revision and an optional `Import` record. Legacy sessions have no attempt evidence until a new attempt occurs. The additive database migration introduces one nullable diagnostic column; it does not alter credentials or existing account authority.

## Interpreting the result

- **Queued** means the resume state was saved, not that a worker has started.
- **Running** identifies an acquired worker attempt and its current saved stage.
- **Completed** means resource import committed; `Ready` and `NoResources` retain their existing meanings.
- **Failed** contains only a fixed classification, a fixed exception category, available numeric HTTP status and locally generated correlation ID.
- **Interrupted** records an interrupted lease. Existing expiry/recovery behavior still controls when work can resume.

A receipt with a revision newer than the displayed status is shown as waiting for current status. The UI does not present the older failure as the result of that receipt. It sends at most one resume for the displayed revision, prevents overlapping submissions, and never resubmits after an uncertain response. Refresh and page reload perform status reads only.

## Privacy and remaining limits

No diagnostic record contains provider response bodies, request/response headers, OAuth state or codes, proof/signature/nonce values, keys, credentials, raw exception messages, arbitrary exception type names or authorization URLs. Stored and received metadata is bounded and validated before display. Normal authorization and installation/account visibility checks still apply.

`ManagedService` HTTP status describes the Windows-to-connection-service response. It does not establish Google's HTTP status or which internal service/provider call failed. `Provider` describes a direct customer-owned provider response. An upstream status whose source cannot be established must not be attributed to Google. This change does not alter the connection-service protocol or publish a package. Finer service-side diagnostics require a separately reviewed change.

The receipt confirms persistence at its returned revision, not successful discovery. If a response is lost and no newer attempt metadata is available, the outcome remains unknown. No UI or transport retry should be inferred from an unchanged status.

## Verification

`PluginImportDiagnosticsTests` covers queue/worker/result correlation, stale and repeated submissions, authorized visibility, cancellation, concurrent cancellation fencing, safe HTTP classification and malformed stored metadata. `PluginImportResumeClientTests` uses a fake HTTP handler to verify one request, bounded receipt validation, legacy behavior and exclusion of raw error bodies. `PluginManageConnectionTests` exercises the actual account page with synthetic clients for overlapping clicks, stale reads, lost acknowledgments, reloads and redaction. `PluginAccountPersistenceTests` verifies the additive migration and model snapshot without opening a live database.

These are synthetic tests. They do not retry the live QFN import, verify Google acceptance or establish QFN channel ownership.

## Deployment boundary

This PR only prepares source and tests. Deploying it requires approval for the normal host database migration and coordinated API, UI and AgentHost update. Apply the additive migration before starting binaries that query its new column. Older binaries tolerate the nullable column, but an older worker does not write new checkpoints; the UI therefore labels diagnostics that disagree with a saved terminal setup state as historical. No migration, restart, service/protocol release or live import is performed by this change.

The production resume client bounds headers, stream acquisition and receipt-body reads with a linked cancellation deadline: at most 30 seconds, or the shorter configured HTTP timeout. Explicit body waits remain bounded even if a stream ignores cancellation. Caller cancellation is preserved. An expired deadline follows the uncertain-acknowledgment UI path, releases the busy state and permits status refresh without another POST.
