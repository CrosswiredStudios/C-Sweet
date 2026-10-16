# Pulse Break planning and escalation recovery

## Confirmed failure

Sweet Sweet Games' Pulse Break project approved its design and collaborative production
brief, hired its initial team and created a planned sprint. No production tickets were
published because Technical Director 4.0.3 exhausted its three planning responses. The
retained responses contain a missing opening quotation mark and premature endings after
`requiredCapabilityKeys:` and inside the 36th item's title (`"title": "Pro`). All three
recorded `finishReason=stop`; the evidence does not establish output-budget exhaustion.

The existing coordination session is `47d13557-9f9f-4185-8ab6-e714e706e7d1` and its Producer
planning commitment is `5fd3ed5d-9036-4ab2-a83c-80d96921cb28`. The session became Blocked at
12:16 Pacific. Manager reports subsequently claimed a corrected retry was being coordinated,
but no new planning attempt existed. Routine reports repeatedly renewed the incident's
review and escalation deadlines, preventing informational delivery to the owner.

## Implementation

Technical Director 4.0.4 `PlanningPages.cs` / `GenerateCheckpointedPlanningAsync` generates
a compact complete index, validates its hierarchy/roles/dependencies, then generates detailed
briefs in pages of three tasks. Every response requests a JSON schema; parsing, duplicate
property checks, required fields, output bounds and semantic checks still apply. Each page
permits three attempts, retaining completion reason and precise failure evidence. Malformed
responses are not repaired by adding brackets, dropped requirements or invented completions.

The input digest binds accepted documents, canonical work, repository facts and authenticated
Producer directions. The frozen index and individual brief pages use separate existing
operating-state records under the 64 KiB platform bound. Revision checks and immutable-page
merges prevent replacement or loss of saved progress. Retry/restart resumes missing pages for
the same inputs. `CompletePlanningProposal` receives only the assembled, validated plan;
partial pages do not create tickets or approve delivery. Core software-role skill normalization
is preserved. No capability, schema migration or permission expansion was introduced.

Platform `ProjectHealthService.RetainManagerRecoveryAsync` preserves the current management
hop and escalation deadline; explanatory reporting cannot rewind the chain or renew its
timeout. `CurrentIncidentExecutionAsync` verifies exact operation correlation, current work,
deadline and unfinished live attempt before identifying a retry as Running. Queued work is
reported separately. `CurrentIncidentStateAsync` exposes this evidence, and `PresentAsync`
projects the authoritative recovery state instead of unsupported retry claims. Exhausted
management chains deliver an informational owner report without inventing decision choices.

## Verification and local rollout

- Technical Director: 145 tests passed with all three full original responses supplied through
  `CSWEET_PLANNING_CAPTURE_ROOT`; no skipped tests. Coverage includes page correction, retained
  scope/criteria, cancellation/restart, exhausted pages, changed inputs and concurrent checkpoints.
  Self-test passed. Packed NuGet metadata and Linux runtime identity both report 4.0.4; release
  notes, manifest, implementation and project version agree.
- Platform: 127 focused health/attention tests passed with no skips, including the three real
  PostgreSQL cases using disposable databases. Coverage includes repeated routine reports,
  deadline preservation, actual collaboration leases, false retry prose, owner status delivery,
  rollback and recovery. Unrelated pre-existing hiring changes were preserved.
- API and AgentHost were stopped through Aspire in a verified idle window and restarted with
  the tested infrastructure assembly. Both report Running; deployed DLL hashes match the tested
  `PlanningRecoveryVerified` output. Rollback copies are retained locally.
- Victor's sole affected installation was upgraded through `AgentDefinitionService.UpdateAsync`,
  with complete manifest parity except version. The artifact is explicitly local development
  source rather than a remote Git release. Runtime digest:
  `sha256:20f8c0a9a32b5370f95771cb2fc358f157e7e9f6eced0f295e8ac97277b3a7fb`.
  Retained source snapshot SHA-256:
  `9b0f47978f5169db757fa5c033b0a72be1e007b0e3acc873ea76c922cfeebac8`.
- At 14:30 Pacific the installation-update recovery reopened the original session (revision 4)
  and owning commitment. The explicit requeue check detected Running and did not issue another
  retry. Victor's new certified runtime established MCP, claimed work
  `fd933c14-cea2-4eed-a670-72599f3c6361`, and started a 4.0.4 planning model call at 14:31:33.
  This confirms resumed execution; publication of the completed backlog is a separate outcome.

Raw responses, source archive, test log and service rollback assemblies are retained under
the ignored local `.artifacts/planning-diagnosis` directory. Business rows, approval decisions,
the original transcript and historical package records were not manually rewritten.
