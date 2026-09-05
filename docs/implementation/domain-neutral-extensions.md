# Domain-neutral platform and publisher-owned extensions

The platform owns generic assignments, coordination envelopes, revisions, evidence, grants, and bounded profile interpretation. Domain terminology and decisions belong to the installed agents and their profiles. A platform package must not reference a publisher's extension implementation.

Profiles may declare `artifactTypes`: up to 128 unique entries with `key`, `displayName`, `schemaVersion`, and an optional `payloadSchema`. Schemas use the same bounded structural subset as profile metadata: object, array, string, integer, number, and boolean, with properties, required fields, items, and additionalProperties. This is declarative validation, not executable policy. Unknown artifact types retain existing envelope validation; registered types must match the profile's schema version and payload structure.

Coordination validates metadata using the organization's workstream and its exact pinned profile version and definition digest. Document details expose the profile's display label; the UI uses a generic identifier fallback. Metadata grants no additional capabilities or permissions. Operating-profile defaults come from the selected agent manifest; C-Sweet no longer overrides them based on industry keywords.

The video-game agents compile publisher-owned extension source under `CrosswiredStudios.VideoGame` from their own immutable repository snapshots. They no longer depend on the unpublished `CSweet.VideoGame.Contracts` or `CSweet.VideoGame.AgentKit` packages. Existing wire type IDs remain unchanged. The Creative Director contributes revision 3 of the existing profile key for new workstreams, adding artifact metadata; the original revision 2 file is preserved for existing workstreams. Existing pinned workstreams are not silently migrated.

Before deployment, commit and publish the changed agent repositories, then import the new agent revisions. Retrying an old immutable commit still builds the old dependency graph. No NuGet publication of a domain package is needed.

Verification completed:
- 28 platform metadata, coordination, profile, and document tests passed.
- 83 tests across 15 affected agents passed.
- All 15 agent self-tests passed.
- All 15 updated agent packages were built and packed; nuspec identities and versions match, and no domain package dependencies remain.
- Restore used an initially empty cache with NuGet.org as the only source and sibling SDK/Memory references disabled.
- All 15 extension source snapshots match their recorded SHA-256 provenance.
- The actual profile revision 3 passes the production validator with 22 declared artifact types.

Deployment status: changes are local and uncommitted. The agent versions are 2.1.1, except Creative Director 1.4.1. Publish/import these source revisions before retrying hiring. No domain NuGet package was published, and no live workstream was migrated.
