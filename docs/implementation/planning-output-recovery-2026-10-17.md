# Planning output size and technical escalation recovery

## Incident

Sweet Games' Breakout Resonance project stopped at coordination session
`ede56c8b-a1cc-434e-9b01-46bd111a9fd6`. Victor 4.2.0 exhausted three outline
responses; the last contained 55,644 characters and exceeded the 40,000-character
response guard. Gabriel 3.2.1 marked commitment `54326cf1-be46-4248-9c84-25954891c2e6`
as a creative conflict because `PlanningReplies.IsTechnicalPlanningGenerationBlock`
recognized an older failure prefix. Production Sprint 1 remained Planned with no tickets.
Compute image preparation was independent of this failure.

## Implementation map

- Technical Director 4.2.1 `PlanningPages.MaximumPlanningPageCharacters` is 128,000.
  `ReadPlanningPageAsync` preserves bounded correction, strict parsing and semantic
  validation. Progressive calls use the configured planning token ceiling rather than
  their former separate 16,000-token cap; provider and configured limits still apply.
- `ProgressiveOutlinePages.ReadOrGenerateScopeOutlineAsync` freezes a complete compact
  identity/dependency manifest, details three Epic/Story increments per page, and maps
  twelve exact approved source paragraphs per coverage page. Manifest and each validated
  page have separate immutable revision-controlled checkpoints. A completion marker binds
  the assembled outline digest. Missing pages resume after failure or restart; corrupted
  or missing completed pages fail closed. No partial outline publishes canonical tickets.
  Existing validated single-record outlines remain readable.
- Platform `PluginOperationsCapabilityHandler.ValidateOperatingState` accepts serialized
  payloads up to 128,000 characters. Storage is already `text`; no migration is needed.
  `McpToolCatalog` permits 1 MiB operating-state requests, 2 MiB coordination response
  requests and 8 MiB coordination read/response results. `AgentCoordinationService.ValidateArtifact`
  permits 1 MiB artifact payloads. `AgentWorkInbox.EnqueueCoreAsync` permits 4 MiB only
  for the native coordination-turn event and source, including its retained transcript;
  unrelated work retains its existing 256 KiB limit. Byte limits accommodate JSON escaping
  and UTF-8 overhead; they are distinct from the planning response character limit.
- Victor emits `[csweet.planning.generator]` for generator/validation failures. Producer
  3.2.2 `PlanningReplies` recognizes that marker and the historical progressive/legacy
  errors. `TechnicalPlanningBlockDescription` retains the exact specialist diagnostic and
  directs C-Sweet code correction through the reporting manager to the CEO.
- Platform `ProjectHealthService.TechnicalPlanning` binds classification to current failed
  collaboration or work records. Legacy misclassified commitments require the exact
  organization, project, board, initiating employee and planning-cycle artifact key.
  Current diagnostic facts and the report source hash carry this classification.
  `ReportAsync`, `ValidateOwnerReportAsync`, `RetainManagerRecoveryAsync` and `PresentAsync`
  preserve it when managers submit misleading prose. Reports move through the existing
  durable manager/outbox path; they do not create a creative approval decision or claim
  an unverified retry. `DeliverAsync` can append one factual correction to an older
  inaccurate CEO report, preserving original messages and suppressing duplicate corrections.

## Verification and local recovery

- Technical Director: 154 tests and self-test passed against published SDK dependencies.
  Tests cover the exact 128,000/128,001-character boundary, failed-page recovery, multi-page
  outline and coverage reconstruction, saved-page integrity, bounded Story refinement,
  role/hierarchy/dependency validation and original failure shapes.
- Producer: 371 tests and self-test passed against published SDK dependencies, including
  recognition and diagnostic preservation for the actual retained error.
- Platform: 200 focused health, operating-state, coordination and MCP catalog tests passed
  without skips. Three PostgreSQL health tests passed against disposable databases.
  Coverage includes report escalation through Producer/Director/Chief to CEO, false retry
  assertions, historical-report correction, idempotency, exact source binding and escaped
  128,000-character coordination handoff. Older coordination fixtures now persist their
  runtime identity, as required by inbox resource reads.
- NuGet metadata and verified Linux `.csab` bundles report Technical Director 4.2.1 and
  Producer 3.2.2. Manifest, implementation, project version and release notes agree.
- The two existing Sweet Games installations were upgraded through
  `AgentDefinitionService.UpdateAsync` after verifying full manifest equality except version.
  These are explicitly local development artifacts, with retained source snapshots under
  ignored `.artifacts/stall-diagnosis`; no remote release is claimed.
- The API and AgentHost were stopped in an idle window, their tested infrastructure and
  broker binaries installed with rollback copies and verified hashes, and their Aspire
  executable resources recreated with the existing configuration. Preparation files and
  independent compute installer processes were preserved.
- Both existing CEO incidents received corrected informational reports through their
  authorized reporters and `ProjectHealthService`. Historical reports remain. The same
  coordination session resumed through `ResumeForManagerAsync`; Gabriel's existing
  commitment was requeued through `IPersonalTodoService.RequeueAsync`.
- At 00:14:48 Pacific on October 17, Victor 4.2.1 started real model run
  `f80c0763-524e-49f7-9d39-f9eedbe0d9a5` against leased coordination work
  `7b6a64b0-4db0-4444-8a69-9908b22f0229`. Both agent installations report Current,
  both runtimes established MCP, and each CEO incident has exactly one corrected
  technical report. Compute preparation retains its original running attempt.
- Verified bundle SHA-256 values are
  `44f905b5a45ea7ad04254a48ed41b715d3a4a803171cab593d57a9886b741aba`
  (Victor) and `383def04efc58b1fb192de35a66cab8b170a70bd878670707f614eb04b3d5a38`
  (Gabriel). Retained source snapshot SHA-256 values are
  `58a5cc03c2d9844a86c989a0503f97ab81175bb17c96bd4ff36b6a97901f6699`
  and `6829d17d6a46b5459bf2961c74ed1152e6a4548fae67d680ed99728f2899e9a1`, respectively.

Local recovery initiation does not establish a published backlog, active sprint or playable
build. Those remain separate outcomes governed by the existing planning, staffing, estimate,
QA readiness and delivery checks.
