# Hierarchical delivery validation and package handoff

Verified on 2026-10-06 against the final local changes. No package publication, application deployment, public release or existing-database deletion was performed.

## Verified results

| Check | Result |
| --- | --- |
| Full platform suite using sibling sources | 3,107 passed, 24 skipped, 0 failed |
| Final full platform suite using packages, including both release UI cases | 3,108 passed, 24 skipped, 0 failed |
| Fresh PostgreSQL migrations and game/software code/document journeys, plus project UI | 22 passed, 0 skipped, 0 failed |
| SDK suite and HelloAgent sample | 286 + 2 passed, 0 failed |
| Contracts suite | 29 passed, 0 failed |
| Nineteen agent package-consumer suites | 1,005 passed, 0 skipped, 0 failed |
| Nineteen agent self-tests and package operations | All passed |
| Generated SDK authoring template | 7 tests and self-test passed |
| Platform and Git host package-only builds | Both passed; all sibling-reference flags disabled |
| Package metadata, dependencies, agent release notes | 21 packages and 19 agent versions verified |
| Game extension provenance | 15 version 1.1.0 manifests match local file digests |
| Whitespace checks | All 22 changed repositories passed |

The full-suite skip count includes environment-dependent integration suites and explicitly skipped existing tests. The five new PostgreSQL scenarios ran without skips in the dedicated pass. PostgreSQL tests use only randomized, loopback `delivery_test_*` databases and remove only databases created by that test run. Tests do not establish deployment readiness for a live installation.

`ProjectsWorkspaceTests.DeliveryRendersExactCandidateAndPartialPromotionWithoutStandaloneMergeColumns` renders both initial validation and a real service fixture with one successful and one failed repository promotion. `HierarchicalTaskRuntimeTests.RunJourney` exercises the shared task runtime, reviewed code rework, exact document QA and temporary reviewer access. The broader symbol map is in [branch recovery and clean installation](branch-recovery-and-clean-install.md).

## Local packages and logs

The local handoff feed is `artifacts/hierarchical-packages`. It contains WorkManagement.Contracts **3.25.0**, Agent.SDK **3.59.0**, and these agents:

| Agent | Version |
| --- | --- |
| CreativeDirector.VideoGame | 1.15.0 |
| Producer.VideoGame | 2.18.0 |
| TechnicalDirector.VideoGame | 2.14.0 |
| Engineer.VideoGame | 3.2.0 |
| QA.VideoGame | 2.7.0 |
| BuildReleaseEngineer.VideoGame | 2.4.0 |
| ArtDirector.VideoGame, Artist.VideoGame, AudioDesigner.VideoGame | 2.4.0 each |
| GameDesigner, LevelDesigner.VideoGame, NarrativeDesigner.VideoGame | 2.4.0 each |
| PlaytestResearcher.VideoGame, TechnicalArtist.VideoGame, UiUxAccessibilityDesigner.VideoGame | 2.4.0 each |
| SoftwareProductManager | 2.20.0 |
| SoftwareArchitect | 0.18.0 |
| SoftwareDeveloper | 1.15.0 |
| SoftwareQA | 1.2.0 |

`artifacts/hierarchical-validation/packages.json` records package IDs, versions and SHA-256 digests. `agents.json`, `agent-test-counts.json`, `platform-package-consumers.json` and individual logs retain validation results. These generated artifacts are local; keep them with the handoff when transferring packages.

Reproduce package-consumer validation with `scripts/verify-hierarchical-agents.py`, `scripts/verify-hierarchical-platform-packages.py` and `scripts/verify-hierarchical-package-metadata.py`, using the built local feed and installed dependency cache. The platform script checks restored assets are packages rather than sibling project references. The SDK's `scripts/verify-authoring-template.ps1` verifies the generated agent. Database setup and the safe installation sequence are in [branch recovery and clean installation](branch-recovery-and-clean-install.md).

## Operational boundaries

Internal Git supports trusted managed-branch promotion and durable receipt recovery. The GitHub adapter deliberately blocks managed delivery when the provider cannot guarantee atomic source/target checks and orchestration-only branch writes; provider-contract tests verify that it makes no unsafe write.

Partial promotion retains successful receipts, stays incomplete and resumes unfinished operations. Changed unfinished candidates require fresh validation and acceptance. Changes to already promoted repository content or release scope after a default-branch promotion require a follow-up release; successful promotions are never automatically reverted. This restriction is surfaced as an actionable block.

New profiles preserve published V1 contracts and historical workflows. Install on an explicitly selected new database; no running-execution migration is supplied. Merging into the default branch does not authorize deployment or public release.
