# Producer hiring kickoff recovery

The Video Game Producer hire stalled at two independent boundaries:

- Definition-based hiring did not activate the Creative Director's imported Workstream profile.
  The local profile `video-game-production.v2` version 4 remained `Previewed`; the Director's
  `platform.workstream.plan.propose.v2` calls failed with "The requested Workstream profile is not active."
- Producer 2.3.2 ignored its onboarding event. Delivery work completed, but the lifecycle outbox
  remained pending because no acknowledgement or introduction was sent.

Installation and definition-based hiring now share profile activation, including updates. Recovery
reconciles previewed profiles only against enabled, active, approved installations with built and signed
packages. It checks the immutable profile key/version and provider identity. The original importing
package version stays as provenance; a newer package may reuse the same immutable profile.
Retired profiles and uninstalled preview imports remain inactive.

Producer 2.3.3 contacts its authoritative manager and owner conversation, persists kickoff state,
and then acknowledges onboarding. Stable source-event message keys make partial retries safe.
The dispatcher scopes delivery deduplication to a package version while retaining the original event
identity, so an ignored onboarding event can reach an updated package.

Creative Director 1.6.3 accepts a kickoff from the approved team's active, eligible Producer before
project creation. It verifies broker-authenticated sender context and the governed roster, then resumes
the existing setup and handoff workflow. No creative acceptance or spending authority changes.
The Producer's existing document refinement workflow records the shared brief and accepted handoff
before workload-backed staffing proposals.

Deploy the updated host and reimport both agent packages to apply this to existing hires. Host
reconciliation repairs installed previewed profiles; pending onboarding is delivered to the new package.
An actual project setup approval may still be required before the scoped brief discussion begins.
The repair does not approve that proposal or send messages by directly modifying database rows.