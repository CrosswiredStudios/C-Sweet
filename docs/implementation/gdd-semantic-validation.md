# Semantic GDD validation and shared wire contracts

## Source map

- Director `CreativeDesign.ReviewCreativeDesignAsync`: authenticated scope, latest
  counterpart turn, terminal finalization, exact submitted revision and platform acceptance.
- Director `GddSemanticValidation.ReviewGddSemanticallyAsync`: substantive planning
  coverage and scope judgment. `ValidateGddReview` checks reported evidence;
  `GddReviewResult` derives findings and disposition in code.
- Creative Director `CreativeDesignModel.GenerateDesignAsync`: complete draft generation
  in any clear structure, one empty/truncated response correction and durable evidence.
- Optional package `CSweet.Agent.SDK.VideoGame.Contracts`:
  `GameCreativeDesignProtocolV1`, `GameCreativeDesignRequestV1`, `GameCreativeDesignResultV1`,
  `GameCreativeDesignFeedbackV1`, `GameCreativeDesignFindingV1`,
  `GameCreativeDesignFailureV1`, `GameCreativeDesignFailureCodes`, `GameGddCriterionKeys`.

## Content and authority boundaries

GDD content may use prose, tables, alternate outlines, reordered sections or another
readable text structure. The independent Director reviewer judges creative intent,
player experience, gameplay, presentation, planned acceptance, approved constraints
and open decisions. A subject can span multiple passages; no heading name, level,
order, length or author name is a content-acceptance gate. Missing material decisions
need an owner; a future specialist role or future playtest is not automatically a
planning blocker. Completed implementation evidence belongs to later delivery gates.

The author submits a draft, then responds to exact Director findings. Its local guard
only detects absent content or an incomplete provider generation. It does not certify
substantive coverage or approve its own GDD.

The reviewer returns judgments and selected evidence, not IDs, hashes, authority,
finding codes or approval. Code checks that supporting passages occur in the supplied
document, tolerating whitespace/case differences. A malformed or unsupported reviewer
response gets one SDK `AgentModelJson` correction, then an owned blocker. These
failures are not frozen as completed review judgments.

Code derives blocking findings, severity and disposition; informative coverage findings
use `accepted-with-findings`, as required by `ArtifactCapabilityHandler.DecideStructuredAsync`.
The handler's actual returned exact-revision acceptance must be confirmed before
saving accepted GDD state or queuing Producer documentation. A previous immutable
platform rejection cannot be overturned by a model accepting the same revision later.

## Interoperability and recovery

Shared request/result/feedback fields and existing type-key values retain their earlier
wire shape. Partners may implement the documented JSON without referencing the .NET
package. Unknown partner extension properties and platform `documentReferences` do
not change the exchanged identities. All project/team, actor, approved pitch and
exact revision/hash checks still apply.

New generation failures use the `video-game.creative-design.failure.v1` artifact,
bound to project, team and pitch with a stable code. Human summary text may vary or
be localized. The Director resumes known generation failures only on an authorized
non-final turn, relaying exact retained assignment/feedback. Unknown codes, malformed
failure payloads and mismatched scope remain blocked. English matching exists only
for pre-contract persisted SHATTERCHORD failures.

The Director never returns Continue during finalization or substitutes an older
submitted revision for a newer blocker. For an already Failed finalization, the first
manager retry records the retained semantic blocker. Retrying that Blocked session
then relays retained feedback for a fresh bounded author pass.

## Verification and rollout

`CreativeDesignWireTests` covers old request/result/feedback serialization, extension
properties, raw partner JSON and typed failure codes without adding a dependency
from the core SDK or platform to this optional package.

`CreativeDesignRecoveryTests` replays the actual SHATTERCHORD heading-format excerpt
unchanged, plus alternate layouts, empty/output-limit correction, revision binding and
duplicate-delivery recovery. `CreativeDesignTests` and `GddSemanticValidationTests`
cover finalization, scoped typed failures, missing semantic coverage and fabricated
review evidence.

`ThirdPartyGddJourneyTests` drives the real Director callback with raw partner JSON
and equivalent prose/table/outline/JSON documents. It tests acceptance, missing
content, identity/revision/digest rejection, invalid review correction, review
availability and an unchanged prior platform rejection. Models and persistence are
deterministic substitutes; these tests do not certify live model judgment quality.
`CreativeDesignFinalizationJourneyTests` also runs both authored agents and the SDK
finalization validator through bounded recovery and explicit resume.

Publish optional contracts 1.1.0 first, then Director 0.6.9 and Creative Director 4.1.4.
Use published releases and Installed Agents. Local build/package verification does
not alter installed agents, live collaborations, approved artifacts or grants.

Final local validation: all 422 Director tests, 49 Creative Director tests, 15 shared
contract tests and 27 cross-role tests pass (513 total). Both agent self-tests and
all three packs pass. Nuspec and bundled manifest versions match 0.6.9 and 4.1.4,
both agent dependencies pin contracts 1.1.0, and the contract DLL consumed by both
agents matches the final contracts `.nupkg` byte for byte.

The cross-role suite uses its checkout-specific Director repository path and
`CentralPackageTransitivePinningEnabled=false` with sibling SDK/work-contract/memory
references disabled. A task-local NuGet configuration supplied the packed contracts
feed because this checkout's normal source mapping excludes unpublished local versions;
global/repository package-source settings were not modified. All packages remain local
verification artifacts pending normal publication and installation.
